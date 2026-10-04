//COM 壳的入口 只做三件事
//一是导出 CLR 用来找我们的 DllGetClassObject 与 DllCanUnloadNow
//二是实现 IClassFactory 与一个只负责转发回调的 Profiler
//三是把 CLR 的接口包成普通 C 函数给 Rust 用
//判断一律在 Rust 侧 这里不写业务逻辑

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <new>
#include <string>
#include <unknwn.h>
#include <cor.h>
#include <corprof.h>

#include "com_base.h"

//导出符号交给链接器参数 这里只保证不修饰名字
//Windows 上不能加 dllexport 否则与 combaseapi.h 里 DllGetClassObject 的已有声明链接不一致
#define LH_EXPORT extern "C"

//Lead.Hook profiler 的标识 必须与 CORECLR_PROFILER 环境变量里的值一致
//{7A2E4C1B-9D3F-4E58-A6B0-1C5D8F2A3E70}
static const GUID CLSID_LeadHookProfiler = {
    0x7a2e4c1b, 0x9d3f, 0x4e58, {0xa6, 0xb0, 0x1c, 0x5d, 0x8f, 0x2a, 0x3e, 0x70}};

//CLR 交过来的 ICorProfilerInfo 接口 门面函数要用
static ICorProfilerInfo7* g_profiler_info = nullptr;

//Rust 侧导出的回调 壳只做搬运
extern "C" {
int32_t lh_on_initialize(void* profiler_info_unknown);
void lh_on_shutdown();
void lh_on_module_load_finished(uint64_t module_id, int32_t hr_status);
void lh_on_module_unload_started(uint64_t module_id);
int32_t lh_on_get_rejit_parameters(uint64_t module_id, uint32_t method_def, void* function_control);
}

namespace lead_hook {

//IUnknown 三件套 计数归零不销毁对象 profiler 常驻到进程结束 免得在关闭期出事
ULONG STDMETHODCALLTYPE ComBase::AddRef()
{
    return ++ref_count_;
}

ULONG STDMETHODCALLTYPE ComBase::Release()
{
    return --ref_count_;
}

HRESULT STDMETHODCALLTYPE ComBase::QueryInterface(REFIID riid, void** ppvObject)
{
    if (ppvObject == nullptr)
        return E_POINTER;

    const bool known =
        IsEqualIID(riid, IID_IUnknown) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback2)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback3)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback4)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback5)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback6)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback7)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback8)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback9)) ||
        IsEqualIID(riid, __uuidof(ICorProfilerCallback10));

    if (!known)
    {
        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    *ppvObject = static_cast<ICorProfilerCallback10*>(this);
    AddRef();
    return S_OK;
}

//Profiler 只覆盖关心的回调 其余九十来个走 ComBase 的空实现
class Profiler final : public ComBase {
public:
    HRESULT STDMETHODCALLTYPE Initialize(IUnknown* pICorProfilerInfoUnk) override
    {
        return lh_on_initialize(pICorProfilerInfoUnk) == 0 ? S_OK : E_FAIL;
    }

    HRESULT STDMETHODCALLTYPE Shutdown() override
    {
        lh_on_shutdown();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID moduleId, HRESULT hrStatus) override
    {
        lh_on_module_load_finished(static_cast<uint64_t>(moduleId), static_cast<int32_t>(hrStatus));
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID moduleId) override
    {
        lh_on_module_unload_started(static_cast<uint64_t>(moduleId));
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID moduleId, mdMethodDef methodId,
                                                 ICorProfilerFunctionControl* pFunctionControl) override
    {
        return lh_on_get_rejit_parameters(static_cast<uint64_t>(moduleId), static_cast<uint32_t>(methodId),
                                          pFunctionControl) == 0
                   ? S_OK
                   : E_FAIL;
    }
};

//ClassFactory 按 CLR 的要求造 Profiler 实例
class ClassFactory final : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
    {
        if (ppvObject == nullptr)
            return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IClassFactory)))
        {
            *ppvObject = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++ref_count_;
    }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG count = --ref_count_;
        if (count == 0)
            delete this;
        return count;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppvObject) override
    {
        if (ppvObject == nullptr)
            return E_POINTER;
        *ppvObject = nullptr;
        if (pUnkOuter != nullptr)
            return CLASS_E_NOAGGREGATION;

        auto* profiler = new (std::nothrow) Profiler();
        if (profiler == nullptr)
            return E_OUTOFMEMORY;

        const HRESULT hr = profiler->QueryInterface(riid, ppvObject);
        profiler->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL fLock) override
    {
        //没有常驻计数的需求 认下来即可
        (void)fLock;
        return S_OK;
    }

private:
    std::atomic<ULONG> ref_count_{1};
};

}

//门面 Rust 要调 CLR 的 API 全走这里 Rust 侧因此一行 COM 都不用写
LH_EXPORT int32_t lh_set_event_mask(uint32_t low_mask, uint32_t high_mask)
{
    if (g_profiler_info == nullptr)
        return -1;
    return g_profiler_info->SetEventMask2(low_mask, high_mask) == S_OK ? 0 : -1;
}

//lh_resolve_profiler_info 把 Initialize 拿到的 IUnknown 换成可用的 ICorProfilerInfo7
LH_EXPORT int32_t lh_resolve_profiler_info(void* profiler_info_unknown)
{
    if (profiler_info_unknown == nullptr)
        return -1;
    if (g_profiler_info != nullptr)
        return 0;

    auto* unknown = static_cast<IUnknown*>(profiler_info_unknown);
    const HRESULT hr = unknown->QueryInterface(__uuidof(ICorProfilerInfo7), reinterpret_cast<void**>(&g_profiler_info));
    return hr == S_OK ? 0 : -1;
}

//LogNative 把一行诊断写进与 Rust 侧同一个日志文件
//元数据注入这条路的中间结果只有 C++ 侧看得到 排查时用
static void LogNative(const std::string& text)
{
    std::ofstream file("lead_hook_native.log", std::ios::app);
    if (!file)
        return;
    file << "[lead-hook/native] " << text << "\n";
}

//Hex4 把 HRESULT 与 token 写成十六进制 便于与官方文档里的码对照
static std::string Hex4(unsigned value)
{
    char buffer[16] = {};
    snprintf(buffer, sizeof(buffer), "0x%08X", value);
    return buffer;
}

//ToWide 类型名与方法名都是 ASCII 逐字节扩展即可 不引平台 API 便于跨平台
static std::wstring ToWide(const char* text)
{
    std::wstring wide;
    if (text == nullptr)
        return wide;
    for (const char* cursor = text; *cursor != '\0'; ++cursor)
        wide.push_back(static_cast<wchar_t>(static_cast<unsigned char>(*cursor)));
    return wide;
}

//ToNarrow 模块名要回传给 Rust 转成窄字符
static void ToNarrow(const WCHAR* source, char* buffer, uint32_t capacity)
{
    uint32_t index = 0;
    for (; source[index] != L'\0' && index + 1 < capacity; ++index)
        buffer[index] = static_cast<char>(source[index]);
    buffer[index] = '\0';
}

//lh_get_module_name 取模块文件名 用来判断是不是要注入的那一个
LH_EXPORT int32_t lh_get_module_name(uint64_t module_id, char* buffer, uint32_t capacity)
{
    if (g_profiler_info == nullptr || buffer == nullptr || capacity == 0)
        return -1;

    WCHAR name[512] = {};
    ULONG length = 0;
    const HRESULT hr = g_profiler_info->GetModuleInfo(static_cast<ModuleID>(module_id), nullptr, 512,
                                                      &length, name, nullptr);
    if (hr != S_OK)
        return -1;

    ToNarrow(name, buffer, capacity);
    return 0;
}

//lh_find_method 在指定模块里按类型名与方法名找方法 返回元数据 token 找不到返回 0
LH_EXPORT uint32_t lh_find_method(uint64_t module_id, const char* type_name, const char* method_name)
{
    if (g_profiler_info == nullptr || type_name == nullptr || method_name == nullptr)
        return 0;

    IMetaDataImport* import = nullptr;
    //用 cor.h 里 EXTERN_GUID 定义的 IID_IMetaDataImport
    //IMetaDataImport 本身没有 MIDL 的 uuid 属性 取不了 __uuidof
    const HRESULT import_hr = g_profiler_info->GetModuleMetaData(
        static_cast<ModuleID>(module_id), ofRead, IID_IMetaDataImport, reinterpret_cast<IUnknown**>(&import));
    if (import_hr != S_OK || import == nullptr)
        return 0;

    const std::wstring wide_type = ToWide(type_name);
    const std::wstring wide_method = ToWide(method_name);

    uint32_t found = 0;
    mdTypeDef type_def = mdTypeDefNil;
    if (import->FindTypeDefByName(wide_type.c_str(), mdTokenNil, &type_def) == S_OK)
    {
        HCORENUM enumerator = nullptr;
        mdMethodDef methods[32] = {};
        ULONG fetched = 0;
        while (found == 0 && import->EnumMethods(&enumerator, type_def, methods, 32, &fetched) == S_OK && fetched > 0)
        {
            for (ULONG i = 0; i < fetched && found == 0; ++i)
            {
                WCHAR name[256] = {};
                ULONG name_length = 0;
                if (import->GetMethodProps(methods[i], nullptr, name, 256, &name_length, nullptr, nullptr, nullptr,
                                           nullptr, nullptr) == S_OK &&
                    wide_method == name)
                {
                    found = methods[i];
                }
            }
        }
        import->CloseEnum(enumerator);
    }

    import->Release();
    return found;
}

//FindOrCreateAssemblyRef 元数据里已有同名项就复用 没有才新建
//重复建会在表里留下多条同名记录 后续 token 会跟托管侧算的错位
static mdAssemblyRef FindOrCreateAssemblyRef(IMetaDataAssemblyImport* assembly_import,
                                             IMetaDataAssemblyEmit* assembly_emit, const std::wstring& name)
{
    if (assembly_import != nullptr)
    {
        HCORENUM enumerator = nullptr;
        mdAssemblyRef refs[32] = {};
        ULONG fetched = 0;
        while (assembly_import->EnumAssemblyRefs(&enumerator, refs, 32, &fetched) == S_OK && fetched > 0)
        {
            for (ULONG i = 0; i < fetched; ++i)
            {
                WCHAR ref_name[256] = {};
                ULONG name_length = 0;
                if (assembly_import->GetAssemblyRefProps(refs[i], nullptr, nullptr, ref_name, 256, &name_length,
                                                         nullptr, nullptr, nullptr, nullptr) == S_OK &&
                    name == ref_name)
                {
                    assembly_import->CloseEnum(enumerator);
                    return refs[i];
                }
            }
        }
        assembly_import->CloseEnum(enumerator);
    }

    if (assembly_emit == nullptr)
        return mdAssemblyRefNil;

    ASSEMBLYMETADATA metadata = {};
    mdAssemblyRef created = mdAssemblyRefNil;
    if (assembly_emit->DefineAssemblyRef(nullptr, 0, name.c_str(), &metadata, nullptr, 0, 0, &created) != S_OK)
        return mdAssemblyRefNil;
    return created;
}

//lh_ensure_assembly_ref 确保模块元数据里有指向某程序集的引用 返回 AssemblyRef token
LH_EXPORT uint32_t lh_ensure_assembly_ref(uint64_t module_id, const char* assembly_name)
{
    if (g_profiler_info == nullptr || assembly_name == nullptr)
        return 0;

    IMetaDataAssemblyImport* assembly_import = nullptr;
    IMetaDataAssemblyEmit* assembly_emit = nullptr;
    g_profiler_info->GetModuleMetaData(static_cast<ModuleID>(module_id), ofRead, IID_IMetaDataAssemblyImport,
                                       reinterpret_cast<IUnknown**>(&assembly_import));
    g_profiler_info->GetModuleMetaData(static_cast<ModuleID>(module_id), ofRead | ofWrite, IID_IMetaDataAssemblyEmit,
                                       reinterpret_cast<IUnknown**>(&assembly_emit));

    const mdAssemblyRef ref =
        FindOrCreateAssemblyRef(assembly_import, assembly_emit, ToWide(assembly_name));

    if (assembly_emit != nullptr)
        assembly_emit->Release();
    if (assembly_import != nullptr)
        assembly_import->Release();
    return static_cast<uint32_t>(ref);
}

//lh_ensure_type_ref 确保模块元数据里有指向某类型的引用 返回 TypeRef token
LH_EXPORT uint32_t lh_ensure_type_ref(uint64_t module_id, const char* assembly_name, const char* type_name)
{
    if (g_profiler_info == nullptr || assembly_name == nullptr || type_name == nullptr)
        return 0;

    //解析范围必须先定下来
    //FindTypeRef 的头一个参数是解析范围 传 mdTokenNil 只会匹配"没有范围"的 TypeRef
    //我们建的都挂在 AssemblyRef 上 传 nil 就永远查不到已建的那条 每次都想新建
    //第二次新建会失败 于是引用拿不到 已注入的方法会被打回原版
    const uint32_t scope = lh_ensure_assembly_ref(module_id, assembly_name);
    if (scope == 0)
        return 0;

    IMetaDataImport* import = nullptr;
    IMetaDataEmit* emit = nullptr;
    g_profiler_info->GetModuleMetaData(static_cast<ModuleID>(module_id), ofRead, IID_IMetaDataImport,
                                       reinterpret_cast<IUnknown**>(&import));
    g_profiler_info->GetModuleMetaData(static_cast<ModuleID>(module_id), ofRead | ofWrite, IID_IMetaDataEmit,
                                       reinterpret_cast<IUnknown**>(&emit));

    const std::wstring wide = ToWide(type_name);

    //先看模块里有没有现成的 有就复用
    if (import != nullptr)
    {
        mdTypeRef existing = mdTypeRefNil;
        if (import->FindTypeRef(static_cast<mdAssemblyRef>(scope), wide.c_str(), &existing) == S_OK)
        {
            LogNative("type_ref 复用 " + Hex4(static_cast<unsigned>(existing)));
            import->Release();
            if (emit != nullptr)
                emit->Release();
            return static_cast<uint32_t>(existing);
        }
    }

    uint32_t created = 0;
    if (emit != nullptr)
    {
        mdTypeRef type_ref = mdTypeRefNil;
        if (emit->DefineTypeRefByName(static_cast<mdAssemblyRef>(scope), wide.c_str(), &type_ref) == S_OK)
            created = static_cast<uint32_t>(type_ref);
    }

    LogNative("type_ref 新建 " + std::string(type_name) + " scope=" + Hex4(scope) + " created=" + Hex4(created));

    if (import != nullptr)
        import->Release();
    if (emit != nullptr)
        emit->Release();
    return created;
}

//lh_ensure_member_ref 确保模块元数据里有指向某方法的引用 返回 MemberRef token
//签名 blob 由 Rust 侧编好传进来 同签名同名字的引用会被复用
LH_EXPORT uint32_t lh_ensure_member_ref(uint64_t module_id, const char* assembly_name, const char* type_name,
                                        const char* member_name, const uint8_t* signature, uint32_t signature_size)
{
    if (g_profiler_info == nullptr || assembly_name == nullptr || type_name == nullptr || member_name == nullptr ||
        signature == nullptr || signature_size == 0)
        return 0;

    const uint32_t type_ref = lh_ensure_type_ref(module_id, assembly_name, type_name);
    if (type_ref == 0)
        return 0;

    IMetaDataImport* import = nullptr;
    IMetaDataEmit* emit = nullptr;
    g_profiler_info->GetModuleMetaData(static_cast<ModuleID>(module_id), ofRead, IID_IMetaDataImport,
                                       reinterpret_cast<IUnknown**>(&import));
    g_profiler_info->GetModuleMetaData(static_cast<ModuleID>(module_id), ofRead | ofWrite, IID_IMetaDataEmit,
                                       reinterpret_cast<IUnknown**>(&emit));

    const std::wstring wide = ToWide(member_name);

    LogNative("member_ref " + std::string(type_name) + "::" + member_name + " type_ref=" + Hex4(type_ref) +
              " import=" + (import != nullptr ? "有" : "无") + " emit=" + (emit != nullptr ? "有" : "无"));

    HRESULT find_hr = E_FAIL;
    if (import != nullptr)
    {
        mdMemberRef existing = mdMemberRefNil;
        find_hr = import->FindMemberRef(static_cast<mdTypeRef>(type_ref), wide.c_str(), signature, signature_size,
                                        &existing);
        if (find_hr == S_OK)
        {
            import->Release();
            if (emit != nullptr)
                emit->Release();
            return static_cast<uint32_t>(existing);
        }
    }

    HRESULT define_hr = E_FAIL;
    uint32_t created = 0;
    if (emit != nullptr)
    {
        mdMemberRef member_ref = mdMemberRefNil;
        define_hr = emit->DefineMemberRef(static_cast<mdTypeRef>(type_ref), wide.c_str(), signature, signature_size,
                                          &member_ref);
        if (define_hr == S_OK)
            created = static_cast<uint32_t>(member_ref);
    }

    LogNative("member_ref 收尾 find=" + Hex4(static_cast<unsigned>(find_hr)) +
              " define=" + Hex4(static_cast<unsigned>(define_hr)) + " created=" + Hex4(created));

    if (import != nullptr)
        import->Release();
    if (emit != nullptr)
        emit->Release();
    return created;
}

//lh_initialize_current_thread 让 CLR 认得当前线程
//不是 profiler 回调线程的地方要调 profiling API 必须先做这一步 否则会失败
LH_EXPORT int32_t lh_initialize_current_thread()
{
    if (g_profiler_info == nullptr)
        return -1;
    return g_profiler_info->InitializeCurrentThread() == S_OK ? 0 : -1;
}

//lh_request_rejit 让 CLR 在下次调用该方法时重新编译 届时回调 GetReJITParameters 要 IL
LH_EXPORT int32_t lh_request_rejit(uint64_t module_id, uint32_t method_def)
{
    if (g_profiler_info == nullptr || method_def == 0)
        return -1;

    ModuleID modules[1] = {static_cast<ModuleID>(module_id)};
    mdMethodDef tokens[1] = {static_cast<mdMethodDef>(method_def)};
    return g_profiler_info->RequestReJIT(1, modules, tokens) == S_OK ? 0 : -1;
}

//lh_set_il_function_body 把准备好的方法体交给 CLR
LH_EXPORT int32_t lh_set_il_function_body(void* function_control, const uint8_t* body, uint32_t size)
{
    if (function_control == nullptr || body == nullptr || size == 0)
        return -1;

    auto* control = static_cast<ICorProfilerFunctionControl*>(function_control);
    return control->SetILFunctionBody(size, body) == S_OK ? 0 : -1;
}

//lh_get_il_function_body 取方法当前的 IL 字节交给 Rust 解析 返回实际字节数
LH_EXPORT int32_t lh_get_il_function_body(uint64_t module_id, uint32_t method_def, uint8_t* buffer, uint32_t capacity)
{
    if (g_profiler_info == nullptr || buffer == nullptr || capacity == 0)
        return -1;

    LPCBYTE body = nullptr;
    ULONG size = 0;
    const HRESULT hr = g_profiler_info->GetILFunctionBody(static_cast<ModuleID>(module_id),
                                                          static_cast<mdMethodDef>(method_def), &body, &size);
    if (hr != S_OK || body == nullptr || size == 0 || size > capacity)
        return -1;

    memcpy(buffer, body, size);
    return static_cast<int32_t>(size);
}

LH_EXPORT HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppvObject)
{
    if (ppvObject == nullptr)
        return E_POINTER;

    if (!IsEqualCLSID(rclsid, CLSID_LeadHookProfiler))
        return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) lead_hook::ClassFactory();
    if (factory == nullptr)
        return E_OUTOFMEMORY;

    const HRESULT hr = factory->QueryInterface(riid, ppvObject);
    factory->Release();
    return hr;
}

LH_EXPORT HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    //常驻 不卸载
    return S_FALSE;
}
