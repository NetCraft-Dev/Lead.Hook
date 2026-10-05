//! Lead.Hook 的原生注入层
//! C++ 只做 COM 适配与转发 所有判断都在这里

mod il;

use std::ffi::{c_char, c_void, CStr, CString};
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::slice;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Condvar, Mutex, OnceLock};
use std::time::Duration;

//CLR 事件掩码 值取自 corprof.h 里的 COR_PRF_MONITOR 枚举
const COR_PRF_MONITOR_MODULE_LOADS: u32 = 0x0000_0004;
const COR_PRF_ENABLE_REJIT: u32 = 0x0004_0000;
const COR_PRF_DISABLE_ALL_NGEN_IMAGES: u32 = 0x8000_0000;

//托管侧交来的引用先在指令里用哨兵占位 解析成真实 token 后再换掉
//0x70 不是合法的元数据表号 不会跟真实 token 撞
const REFERENCE_SENTINEL: u32 = 0x70FF_0000;

//C++ 壳提供的门面
extern "C" {
    fn lh_set_event_mask(low_mask: u32, high_mask: u32) -> i32;
    fn lh_resolve_profiler_info(profiler_info_unknown: *mut c_void) -> i32;
    fn lh_get_module_name(module_id: u64, buffer: *mut c_char, capacity: u32) -> i32;
    fn lh_find_method(module_id: u64, type_name: *const c_char, method_name: *const c_char) -> u32;
    fn lh_request_rejit(module_id: u64, method_def: u32) -> i32;
    fn lh_set_il_function_body(function_control: *mut c_void, body: *const u8, size: u32) -> i32;
    fn lh_get_il_function_body(module_id: u64, method_def: u32, buffer: *mut u8, capacity: u32) -> i32;
    fn lh_initialize_current_thread() -> i32;
    fn lh_ensure_assembly_ref(module_id: u64, assembly_name: *const c_char) -> u32;
    fn lh_ensure_type_ref(module_id: u64, assembly_name: *const c_char, type_name: *const c_char) -> u32;
    fn lh_ensure_member_ref(
        module_id: u64,
        assembly_name: *const c_char,
        type_name: *const c_char,
        member_name: *const c_char,
        signature: *const u8,
        signature_size: u32,
    ) -> u32;
}

//CLR 按约定从库里找 DllGetClassObject 与 DllCanUnloadNow 这两个导出名
//实现在 C++ 壳里 这里只负责转出去
//不让 C++ 直接导出是因为 cdylib 的符号可见性由 Rust 的链接参数说了算 C++ 侧的全局符号会被降成 local
//走 #[no_mangle] 从 Rust 导出才稳 三个平台一致
extern "system" {
    fn lh_com_get_class_object(
        rclsid: *const c_void,
        riid: *const c_void,
        ppv: *mut *mut c_void,
    ) -> i32;
    fn lh_com_can_unload_now() -> i32;
}

#[no_mangle]
pub unsafe extern "system" fn DllGetClassObject(
    rclsid: *const c_void,
    riid: *const c_void,
    ppv: *mut *mut c_void,
) -> i32 {
    lh_com_get_class_object(rclsid, riid, ppv)
}

#[no_mangle]
pub unsafe extern "system" fn DllCanUnloadNow() -> i32 {
    lh_com_can_unload_now()
}

//RewriteRequest 一条运行时改写请求
//托管侧把备好的新方法体登记进来 等目标模块就位时兑现
struct RewriteRequest {
    //目标模块文件名后缀 用来认模块
    module_suffix: String,
    type_name: String,
    method_name: String,
    //新的 COR_ILMETHOD 字节 由托管侧生成好
    body: Vec<u8>,
    //兑现后回填 用来在 GetReJITParameters 里认领
    module_id: AtomicU64,
    token: AtomicU32,
}

//日志句柄 回调可能来自多条线程 用锁串起来
//打开失败就整个丢掉 日志写不出去不该拖垮被注入的进程
static LOG: OnceLock<Option<Mutex<File>>> = OnceLock::new();
//全部改写请求 与 唤醒兑现线程用的条件变量
static REQUESTS: OnceLock<(Mutex<Vec<RewriteRequest>>, Condvar)> = OnceLock::new();
//已加载模块 (module_id, 文件名) 兑现请求时按它找目标
static MODULES: OnceLock<Mutex<Vec<(u64, String)>>> = OnceLock::new();

fn log(message: &str) {
    let slot = LOG.get_or_init(|| {
        let path = std::env::current_dir().ok()?.join("lead_hook_native.log");
        OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .ok()
            .map(Mutex::new)
    });

    if let Some(handle) = slot {
        if let Ok(mut file) = handle.lock() {
            let _ = writeln!(file, "[lead-hook] {message}");
        }
    }
}

fn requests() -> &'static (Mutex<Vec<RewriteRequest>>, Condvar) {
    REQUESTS.get_or_init(|| (Mutex::new(Vec::new()), Condvar::new()))
}

fn modules() -> &'static Mutex<Vec<(u64, String)>> {
    MODULES.get_or_init(|| Mutex::new(Vec::new()))
}

//wake 有新请求或新模块时叫醒兑现线程
fn wake() {
    requests().1.notify_all();
}

//fulfill_pending 把所有能兑现的请求兑现掉 返回是否还有没兑现的
fn fulfill_pending() -> bool {
    let modules = modules().lock().unwrap();
    let (lock, _) = requests();
    let requests = lock.lock().unwrap();

    for request in requests.iter() {
        //已经兑现过的跳过
        if request.token.load(Ordering::Relaxed) != 0 {
            continue;
        }
        for (module_id, name) in modules.iter() {
            if name.ends_with(&request.module_suffix) {
                fulfill(request, *module_id);
                break;
            }
        }
    }

    requests
        .iter()
        .any(|request| request.token.load(Ordering::Relaxed) == 0)
}

//start_worker 起一条常驻线程专门兑现改写请求
//托管线程里不能直接调 CLR 的 profiling API 会重入出事 统一交给这条线程
fn start_worker() {
    static STARTED: OnceLock<()> = OnceLock::new();
    if STARTED.set(()).is_err() {
        return;
    }

    std::thread::spawn(|| {
        //CLR 初始化收尾要点时间 注册线程失败就退避重试
        for _ in 0..40 {
            if unsafe { lh_initialize_current_thread() } == 0 {
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }

        let (lock, condvar) = requests();
        loop {
            fulfill_pending();

            //没得兑现就挂起 超时兜底 免得条件变量丢了唤醒就卡死
            let guard = lock.lock().unwrap();
            let _waited = condvar.wait_timeout(guard, Duration::from_millis(200)).unwrap();
        }
    });

    log("兑现线程已启动");
}

//fulfill 把一条请求兑现到某个已加载的模块上
//找到方法就发 ReJIT 并把 module_id 与 token 记回请求 供回调认领
fn fulfill(request: &RewriteRequest, module_id: u64) -> bool {
    let type_name = match CString::new(request.type_name.as_str()) {
        Ok(value) => value,
        Err(_) => return false,
    };
    let method_name = match CString::new(request.method_name.as_str()) {
        Ok(value) => value,
        Err(_) => return false,
    };

    let token = unsafe { lh_find_method(module_id, type_name.as_ptr(), method_name.as_ptr()) };
    if token == 0 {
        log(&format!("{}::{} 在模块里没找到", request.type_name, request.method_name));
        return false;
    }

    if unsafe { lh_request_rejit(module_id, token) } != 0 {
        log(&format!("{}::{} RequestReJIT 失败", request.type_name, request.method_name));
        return false;
    }

    request.module_id.store(module_id, Ordering::Relaxed);
    request.token.store(token, Ordering::Relaxed);
    log(&format!(
        "已兑现 {}::{} token=0x{token:08X}",
        request.type_name, request.method_name
    ));
    true
}

//lh_on_initialize CLR 加载完 profiler 后的第一个回调 在这里决定订阅哪些事件
#[no_mangle]
pub extern "C" fn lh_on_initialize(profiler_info_unknown: *mut c_void) -> i32 {
    log("Initialize 进入");

    if unsafe { lh_resolve_profiler_info(profiler_info_unknown) } != 0 {
        log("取 ICorProfilerInfo7 失败");
        return -1;
    }

    //ENABLE_REJIT 必须与 DISABLE_ALL_NGEN_IMAGES 一起给
    //只给前者 SetEventMask 会直接失败 这是官方文档写明的约束
    //JIT 编译事件不订阅 那是每个方法编译完都回一次 我们用不上 订了只会给启动添负担
    let low =
        COR_PRF_MONITOR_MODULE_LOADS | COR_PRF_ENABLE_REJIT | COR_PRF_DISABLE_ALL_NGEN_IMAGES;

    if unsafe { lh_set_event_mask(low, 0) } != 0 {
        log("SetEventMask 失败");
        return -1;
    }

    log(&format!("SetEventMask 成功 low=0x{low:08X}"));

    //兑现线程在这里起 后面登记请求全靠它
    start_worker();
    0
}

#[no_mangle]
pub extern "C" fn lh_on_shutdown() {
    log("Shutdown");
}

//lh_on_module_load_finished 记下模块并叫醒兑现线程 兑现本身不在回调里做
#[no_mangle]
pub extern "C" fn lh_on_module_load_finished(module_id: u64, hr_status: i32) {
    if hr_status != 0 {
        return;
    }

    let mut buffer = [0 as c_char; 512];
    if unsafe { lh_get_module_name(module_id, buffer.as_mut_ptr(), buffer.len() as u32) } != 0 {
        return;
    }
    let name = unsafe { CStr::from_ptr(buffer.as_ptr()) }.to_string_lossy().into_owned();

    modules().lock().unwrap().push((module_id, name));
    wake();
}

#[no_mangle]
pub extern "C" fn lh_on_module_unload_started(module_id: u64) {
    modules().lock().unwrap().retain(|(id, _)| *id != module_id);
    log(&format!("模块卸载 module_id={module_id}"));
}

//lh_request_rewrite 托管侧登记一条运行时改写
//新方法体由托管侧备好 这里只负责在模块就位时把 ReJIT 请求发出去
#[no_mangle]
pub extern "C" fn lh_request_rewrite(
    module_suffix: *const c_char,
    type_name: *const c_char,
    method_name: *const c_char,
    body: *const u8,
    size: u32,
) -> i32 {
    if module_suffix.is_null() || type_name.is_null() || method_name.is_null() || body.is_null() || size == 0
    {
        return -1;
    }

    let module_suffix = unsafe { CStr::from_ptr(module_suffix) }.to_string_lossy().into_owned();
    let type_name = unsafe { CStr::from_ptr(type_name) }.to_string_lossy().into_owned();
    let method_name = unsafe { CStr::from_ptr(method_name) }.to_string_lossy().into_owned();
    let body = unsafe { slice::from_raw_parts(body, size as usize) }.to_vec();

    log(&format!(
        "登记改写 {module_suffix} {type_name}::{method_name} {} 字节",
        body.len()
    ));

    let request = RewriteRequest {
        module_suffix,
        type_name,
        method_name,
        body,
        module_id: AtomicU64::new(0),
        token: AtomicU32::new(0),
    };

    //登记完叫醒兑现线程 真正的 find 与 RequestReJIT 都由它去做
    requests().0.lock().unwrap().push(request);
    wake();
    0
}

//describe 把一条指令写成便于对照的文本
fn describe(instruction: &il::Instruction) -> String {
    let name = il::lookup(instruction.code).map_or("?", |info| info.name);
    match &instruction.operand {
        il::Operand::None => name.to_string(),
        il::Operand::I32(value) => format!("{name} {value}"),
        il::Operand::I64(value) => format!("{name} {value}"),
        il::Operand::F32(value) => format!("{name} {value}"),
        il::Operand::F64(value) => format!("{name} {value}"),
        il::Operand::Var(value) => format!("{name} [{value}]"),
        il::Operand::Token(value) => format!("{name} 0x{value:08X}"),
        il::Operand::Branch(value) => format!("{name} -> {value:04X}"),
        il::Operand::Switch(targets) => format!("{name} -> {targets:?}"),
    }
}

//compare 逐条比对原体与重建后的体 不等价时把首处差异打出来
fn compare(original: &il::MethodBody, reparsed: &il::MethodBody) -> bool {
    if original.instructions.len() != reparsed.instructions.len() {
        log(&format!(
            "重建后指令条数变了 {} -> {}",
            original.instructions.len(),
            reparsed.instructions.len()
        ));
        return false;
    }

    let mut equivalent = true;
    for (index, (left, right)) in original
        .instructions
        .iter()
        .zip(reparsed.instructions.iter())
        .enumerate()
    {
        if left.code == right.code && left.operand == right.operand {
            continue;
        }
        //只打首处 后面的多半是它的连带
        if equivalent {
            log(&format!(
                "第 {index} 条不等价 原 {} 编码 0x{:04X} / 重建 {} 编码 0x{:04X}",
                describe(left),
                left.code,
                describe(right),
                right.code
            ));
        }
        equivalent = false;
    }
    equivalent
}

//read_original 读方法当前的 IL 并解析 取不到或认不出都给 None
fn read_original(module_id: u64, method_def: u32) -> Option<il::MethodBody> {
    let mut raw = vec![0u8; 65536];
    let size = unsafe {
        lh_get_il_function_body(module_id, method_def, raw.as_mut_ptr(), raw.len() as u32)
    };
    if size <= 0 {
        log("取不到原方法体");
        return None;
    }
    raw.truncate(size as usize);

    match il::MethodBody::parse(&raw) {
        Ok(parsed) => Some(parsed),
        Err(error) => {
            log(&format!("原方法体解析失败 {error}"));
            None
        }
    }
}

//dump_original 读原方法体解析一遍再原样重建 比对两者是否等价
//只做自检 确认解析器认得 CLR 真正给出来的字节 不参与改写决策
fn dump_original(module_id: u64, method_def: u32) {
    let Some(body) = read_original(module_id, method_def) else {
        return;
    };

    log(&format!(
        "原方法体 指令 {} 条 max_stack={} local_sig=0x{:08X} fat={} init_locals={}",
        body.instructions.len(),
        body.max_stack,
        body.local_var_sig_tok,
        body.was_fat,
        body.init_locals
    ));
    for instruction in &body.instructions {
        log(&format!("  {:04X}: {}", instruction.offset, describe(instruction)));
    }

    let rebuilt = body.encode();
    let equivalent = match il::MethodBody::parse(&rebuilt) {
        Ok(reparsed) => compare(&body, &reparsed),
        Err(error) => {
            log(&format!("重建体解析失败 {error}"));
            false
        }
    };
    log(&format!(
        "重建 {} 字节 指令序列等价={}",
        rebuilt.len(),
        equivalent
    ));
}

//to_instructions 把描述里的指令转成 IL 指令
//分支目标直接拿指令索引当偏移用 索引唯一 编码时按它匹配就够
fn to_instructions(body: &il::wire::WireBody) -> Vec<il::Instruction> {
    body.instructions
        .iter()
        .enumerate()
        .map(|(index, instruction)| {
            let operand = match &instruction.operand {
                il::wire::WireOperand::None => il::Operand::None,
                il::wire::WireOperand::I32(value) => il::Operand::I32(*value),
                il::wire::WireOperand::I64(value) => il::Operand::I64(*value),
                il::wire::WireOperand::F32(value) => il::Operand::F32(*value),
                il::wire::WireOperand::F64(value) => il::Operand::F64(*value),
                il::wire::WireOperand::Var(value) => il::Operand::Var(*value),
                il::wire::WireOperand::Branch(target) => il::Operand::Branch(*target as u32),
                il::wire::WireOperand::Switch(targets) => {
                    il::Operand::Switch(targets.iter().map(|target| *target as u32).collect())
                }
                il::wire::WireOperand::Token(token) => il::Operand::Token(*token),
                //引用先用哨兵占位 解析出真实 token 后再换掉
                il::wire::WireOperand::Ref(reference) => {
                    il::Operand::Token(REFERENCE_SENTINEL | *reference as u32)
                }
            };
            il::Instruction {
                offset: index as u32,
                code: instruction.code,
                operand,
            }
        })
        .collect()
}

//build_body 把托管侧交来的描述变成可以交给 CLR 的方法体
//引用先解析成运行时 token 再替换指令里的哨兵 最后按 fat 形式编出来
fn build_body(module_id: u64, method_def: u32, description: &[u8]) -> Result<Vec<u8>, String> {
    let described = il::wire::parse(description)?;

    let resolve_type = |assembly: &str, name: &str| -> Result<u32, String> {
        let assembly_name = CString::new(assembly).map_err(|_| "程序集名里有空字节".to_string())?;
        let type_name = CString::new(name).map_err(|_| "类型名里有空字节".to_string())?;
        let token = unsafe { lh_ensure_type_ref(module_id, assembly_name.as_ptr(), type_name.as_ptr()) };
        if token == 0 {
            Err(format!("拿不到类型引用 {name}"))
        } else {
            Ok(token)
        }
    };

    let mut tokens = Vec::with_capacity(described.references.len());
    for reference in &described.references {
        let signature = il::wire::encode_signature(reference, &resolve_type)?;
        let assembly_name =
            CString::new(reference.assembly.as_str()).map_err(|_| "程序集名里有空字节".to_string())?;
        let type_name =
            CString::new(reference.type_name.as_str()).map_err(|_| "类型名里有空字节".to_string())?;
        let member_name =
            CString::new(reference.member.as_str()).map_err(|_| "成员名里有空字节".to_string())?;

        let token = unsafe {
            lh_ensure_member_ref(
                module_id,
                assembly_name.as_ptr(),
                type_name.as_ptr(),
                member_name.as_ptr(),
                signature.as_ptr(),
                signature.len() as u32,
            )
        };
        if token == 0 {
            return Err(format!(
                "拿不到成员引用 {}::{}",
                reference.type_name, reference.member
            ));
        }

        log(&format!(
            "引用 {}::{} 解析为 token=0x{token:08X} 签名 {} 字节",
            reference.type_name,
            reference.member,
            signature.len()
        ));
        tokens.push(token);
    }

    let mut instructions = to_instructions(&described);
    for instruction in &mut instructions {
        let il::Operand::Token(value) = instruction.operand else {
            continue;
        };
        if value & 0xFFFF_0000 != REFERENCE_SENTINEL {
            continue;
        }
        let index = (value & 0xFFFF) as usize;
        let token = tokens
            .get(index)
            .ok_or_else(|| format!("引用索引 {index} 越界"))?;
        instruction.operand = il::Operand::Token(*token);
    }

    //局部变量签名要么沿用原方法体 要么干脆没有
    //这一版还不支持给新方法体注入局部变量签名
    let original = read_original(module_id, method_def);
    let carries_locals = described.flags & 1 != 0 && original.is_some();

    let local_var_sig_tok = match (&original, carries_locals) {
        (Some(body), true) => body.local_var_sig_tok,
        _ => 0,
    };
    let init_locals = carries_locals && original.as_ref().is_some_and(|body| body.init_locals);

    //栈深给点余量 托管侧算偏小会让 JIT 判方法非法
    let max_stack = described
        .max_stack
        .max(original.as_ref().map_or(0, |body| body.max_stack))
        .saturating_add(8);

    let body = il::MethodBody {
        max_stack,
        local_var_sig_tok,
        init_locals,
        instructions,
        was_fat: true,
    };
    Ok(body.encode())
}

//lh_on_get_rejit_parameters 该给 CLR 提供替换用 IL 的地方
//这个回调是 ReJIT 真正发生的那一刻 CLR 在这里等我们把新方法体交出去
#[no_mangle]
pub extern "C" fn lh_on_get_rejit_parameters(
    module_id: u64,
    method_def: u32,
    function_control: *mut c_void,
) -> i32 {
    log(&format!(
        "ReJIT 参数请求 module_id={module_id} token=0x{method_def:08X}"
    ));

    //按 module_id 与 token 认领是哪条请求
    let requests = requests().0.lock().unwrap();
    let matched = requests.iter().find(|request| {
        request.token.load(Ordering::Relaxed) == method_def
            && request.module_id.load(Ordering::Relaxed) == module_id
    });

    let Some(request) = matched else {
        log("没有对应的改写请求 交回原版");
        return -1;
    };

    //捎带把原方法体拆一遍 确认解析器认得 CLR 给出来的字节
    dump_original(module_id, method_def);

    //描述里的引用要在这里解析成 token 所以必须等到这个回调 此时模块一定已经就位
    let built = match build_body(module_id, method_def, &request.body) {
        Ok(bytes) => bytes,
        Err(error) => {
            log(&format!("组装方法体失败 {error} 交回原版"));
            return -1;
        }
    };

    let rc = unsafe { lh_set_il_function_body(function_control, built.as_ptr(), built.len() as u32) };

    log(&format!("提交 {} 字节 返回 {rc}", built.len()));
    rc
}
