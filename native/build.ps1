# 编译原生 profiler 层并把产物落到 native/out/<rid>/ 供 csproj 打包
# 用法 ./build.ps1                编译当前平台
# 用法 ./build.ps1 -Target <triple>  编译指定目标 需要该 target 已用 rustup 安装
# 其它平台的产物由 .github/workflows/release.yml 的矩阵在对应 runner 上编出来

param(
    [string]$Target = ""
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Output = Join-Path $Root "out"

#没给 target 就用本机默认的 从 rustc 的 host 里读 不猜平台
if ([string]::IsNullOrWhiteSpace($Target)) {
    $Target = (& rustc -vV | Select-String "^host:").ToString().Split(":")[1].Trim()
    if ([string]::IsNullOrWhiteSpace($Target)) {
        throw "could not read the host target from rustc -vV"
    }
}

#triple 到 nuget 的 rid 与产物文件名
#cdylib 在 windows 上不带前缀 linux 是 lib*.so macos 是 lib*.dylib
$Known = @{
    "x86_64-pc-windows-msvc"    = @{ Rid = "win-x64";   File = "lead_hook_native.dll" }
    "x86_64-unknown-linux-gnu"  = @{ Rid = "linux-x64"; File = "liblead_hook_native.so" }
    "x86_64-apple-darwin"       = @{ Rid = "osx-x64";   File = "liblead_hook_native.dylib" }
    "aarch64-unknown-linux-gnu" = @{ Rid = "linux-arm64"; File = "liblead_hook_native.so" }
    "aarch64-apple-darwin"      = @{ Rid = "osx-arm64"; File = "liblead_hook_native.dylib" }
}

if (-not $Known.ContainsKey($Target)) {
    throw "no rid mapping for target $Target, add one to this script"
}

$info = $Known[$Target]
Write-Host "==> cargo build --release --target $Target"

Push-Location $Root
try {
    & cargo build --release --target $Target
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$built = Join-Path $Root "target\$Target\release\$($info.File)"
if (-not (Test-Path $built)) {
    throw "cargo reported success but $built is missing"
}

$destination = Join-Path $Output $info.Rid
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item $built (Join-Path $destination $info.File) -Force

Write-Host "==> $($info.Rid) -> $(Join-Path $destination $info.File)"
