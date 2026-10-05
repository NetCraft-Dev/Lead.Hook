//编译 C++ 的 COM 薄壳并链进 Rust 的 cdylib
//coreclr 的头已内嵌在 third_party 下 工程不依赖任何外部路径
fn main() {
    println!("cargo:rerun-if-changed=shell/com_base.cpp");
    println!("cargo:rerun-if-changed=shell/com_base.h");
    println!("cargo:rerun-if-changed=shell/entry.cpp");

    let windows = std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows");

    let mut build = cc::Build::new();
    build.cpp(true).std("c++20");
    //源码里有中文注释 MSVC 默认按本地代码页读会把它们解析坏
    //加上这个才对 GCC/Clang 无效 它们本来就按 UTF-8 读
    build.flag_if_supported("/utf-8");
    build.include("third_party/coreclr");

    //库里的头分两套 Windows 上 unknwn.h/windows.h 这些由 SDK 提供
    //非 Windows 没有 SDK coreclr 把它们另收在 pal/inc 与 pal/inc/rt 下 得用它的那一份
    if !windows {
        build.include("third_party/coreclr/rt");
        build.include("third_party/coreclr/pal");

        //PAL 的头靠 HOST_* 挑架构与系统相关的定义 平时这些由 coreclr 的 CMake 传进来
        //少一个就会撞上 pal.h 里那句 #error Unknown architecture
        match std::env::var("CARGO_CFG_TARGET_ARCH").as_deref() {
            Ok("x86_64") => {
                build.define("HOST_AMD64", None);
                build.define("HOST_64BIT", None);
            }
            Ok("aarch64") => {
                build.define("HOST_ARM64", None);
                build.define("HOST_64BIT", None);
            }
            Ok("x86") => {
                build.define("HOST_X86", None);
            }
            Ok("arm") => {
                build.define("HOST_ARM", None);
            }
            _ => {}
        }
        build.define("HOST_UNIX", None);
        if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
            build.define("HOST_OSX", None);
        } else {
            build.define("HOST_LINUX", None);
        }
    }

    build
        .file("shell/com_base.cpp")
        .file("shell/entry.cpp")
        .compile("lead_hook_shell");
}
