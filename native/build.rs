//编译 C++ 的 COM 薄壳并链进 Rust 的 cdylib
//coreclr 的头已内嵌在 third_party 下 工程不依赖任何外部路径
fn main() {
    println!("cargo:rerun-if-changed=shell/com_base.cpp");
    println!("cargo:rerun-if-changed=shell/com_base.h");
    println!("cargo:rerun-if-changed=shell/entry.cpp");

    cc::Build::new()
        .cpp(true)
        .std("c++20")
        //源码里有中文注释 MSVC 默认按本地代码页读会把它们解析坏
        //加上这个才对 GCC/Clang 无效 它们本来就按 UTF-8 读
        .flag_if_supported("/utf-8")
        .include("third_party/coreclr")
        .file("shell/com_base.cpp")
        .file("shell/entry.cpp")
        .compile("lead_hook_shell");

    //DllGetClassObject 与 DllCanUnloadNow 没有被任何 Rust 代码引用 链接器会把它们当死代码丢掉
    //这里显式导出 一举两得 既保留符号又让 CLR 能用 GetProcAddress 找到
    //PRIVATE 表示只进导出表不进导入库 这两条本来也不需要别人链接
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        println!("cargo:rustc-link-arg=/EXPORT:DllGetClassObject,PRIVATE");
        println!("cargo:rustc-link-arg=/EXPORT:DllCanUnloadNow,PRIVATE");
    }
}
