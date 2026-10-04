//此文件由 __gen_com_base.py 从 corprof.h 生成 不要手改
//来源 dotnet/runtime 10.0.9 src/coreclr/pal/prebuilt/inc/corprof.h  MIT
//把 ICorProfilerCallback10 的继承链摊平 未关心的方法一律返回 S_OK
//槽位必须齐全 少一个类就是抽象的 顺序交给编译器保证
//参数只留类型不留名字 声明与定义一致即可 免得一堆未使用参数的警告

#ifndef LEAD_HOOK_COM_BASE_H
#define LEAD_HOOK_COM_BASE_H

#include <atomic>
#include <unknwn.h>
#include <cor.h>
#include <corprof.h>

namespace lead_hook {

//ComBase 提供一个可以被实例化的 ICorProfilerCallback10
//真正要做事的回调由子类 override 其余走这里的默认实现
class ComBase : public ICorProfilerCallback10 {
public:
    ComBase();
    virtual ~ComBase();

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;

    HRESULT STDMETHODCALLTYPE Initialize(IUnknown*) override;
    HRESULT STDMETHODCALLTYPE Shutdown(void) override;
    HRESULT STDMETHODCALLTYPE AppDomainCreationStarted(AppDomainID) override;
    HRESULT STDMETHODCALLTYPE AppDomainCreationFinished(AppDomainID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE AppDomainShutdownStarted(AppDomainID) override;
    HRESULT STDMETHODCALLTYPE AppDomainShutdownFinished(AppDomainID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE AssemblyLoadStarted(AssemblyID) override;
    HRESULT STDMETHODCALLTYPE AssemblyLoadFinished(AssemblyID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE AssemblyUnloadStarted(AssemblyID) override;
    HRESULT STDMETHODCALLTYPE AssemblyUnloadFinished(AssemblyID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE ModuleLoadStarted(ModuleID) override;
    HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID) override;
    HRESULT STDMETHODCALLTYPE ModuleUnloadFinished(ModuleID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE ModuleAttachedToAssembly(ModuleID, AssemblyID) override;
    HRESULT STDMETHODCALLTYPE ClassLoadStarted(ClassID) override;
    HRESULT STDMETHODCALLTYPE ClassLoadFinished(ClassID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE ClassUnloadStarted(ClassID) override;
    HRESULT STDMETHODCALLTYPE ClassUnloadFinished(ClassID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE FunctionUnloadStarted(FunctionID) override;
    HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID, BOOL) override;
    HRESULT STDMETHODCALLTYPE JITCompilationFinished(FunctionID, HRESULT, BOOL) override;
    HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchStarted(FunctionID, BOOL*) override;
    HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchFinished(FunctionID, COR_PRF_JIT_CACHE) override;
    HRESULT STDMETHODCALLTYPE JITFunctionPitched(FunctionID) override;
    HRESULT STDMETHODCALLTYPE JITInlining(FunctionID, FunctionID, BOOL*) override;
    HRESULT STDMETHODCALLTYPE ThreadCreated(ThreadID) override;
    HRESULT STDMETHODCALLTYPE ThreadDestroyed(ThreadID) override;
    HRESULT STDMETHODCALLTYPE ThreadAssignedToOSThread(ThreadID, DWORD) override;
    HRESULT STDMETHODCALLTYPE RemotingClientInvocationStarted(void) override;
    HRESULT STDMETHODCALLTYPE RemotingClientSendingMessage(GUID*, BOOL) override;
    HRESULT STDMETHODCALLTYPE RemotingClientReceivingReply(GUID*, BOOL) override;
    HRESULT STDMETHODCALLTYPE RemotingClientInvocationFinished(void) override;
    HRESULT STDMETHODCALLTYPE RemotingServerReceivingMessage(GUID*, BOOL) override;
    HRESULT STDMETHODCALLTYPE RemotingServerInvocationStarted(void) override;
    HRESULT STDMETHODCALLTYPE RemotingServerInvocationReturned(void) override;
    HRESULT STDMETHODCALLTYPE RemotingServerSendingReply(GUID*, BOOL) override;
    HRESULT STDMETHODCALLTYPE UnmanagedToManagedTransition(FunctionID, COR_PRF_TRANSITION_REASON) override;
    HRESULT STDMETHODCALLTYPE ManagedToUnmanagedTransition(FunctionID, COR_PRF_TRANSITION_REASON) override;
    HRESULT STDMETHODCALLTYPE RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON) override;
    HRESULT STDMETHODCALLTYPE RuntimeSuspendFinished(void) override;
    HRESULT STDMETHODCALLTYPE RuntimeSuspendAborted(void) override;
    HRESULT STDMETHODCALLTYPE RuntimeResumeStarted(void) override;
    HRESULT STDMETHODCALLTYPE RuntimeResumeFinished(void) override;
    HRESULT STDMETHODCALLTYPE RuntimeThreadSuspended(ThreadID) override;
    HRESULT STDMETHODCALLTYPE RuntimeThreadResumed(ThreadID) override;
    HRESULT STDMETHODCALLTYPE MovedReferences(ULONG, ObjectID[], ObjectID[], ULONG[]) override;
    HRESULT STDMETHODCALLTYPE ObjectAllocated(ObjectID, ClassID) override;
    HRESULT STDMETHODCALLTYPE ObjectsAllocatedByClass(ULONG, ClassID[], ULONG[]) override;
    HRESULT STDMETHODCALLTYPE ObjectReferences(ObjectID, ClassID, ULONG, ObjectID[]) override;
    HRESULT STDMETHODCALLTYPE RootReferences(ULONG, ObjectID[]) override;
    HRESULT STDMETHODCALLTYPE ExceptionThrown(ObjectID) override;
    HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionEnter(FunctionID) override;
    HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionLeave(void) override;
    HRESULT STDMETHODCALLTYPE ExceptionSearchFilterEnter(FunctionID) override;
    HRESULT STDMETHODCALLTYPE ExceptionSearchFilterLeave(void) override;
    HRESULT STDMETHODCALLTYPE ExceptionSearchCatcherFound(FunctionID) override;
    HRESULT STDMETHODCALLTYPE ExceptionOSHandlerEnter(UINT_PTR) override;
    HRESULT STDMETHODCALLTYPE ExceptionOSHandlerLeave(UINT_PTR) override;
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionEnter(FunctionID) override;
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionLeave(void) override;
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyEnter(FunctionID) override;
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyLeave(void) override;
    HRESULT STDMETHODCALLTYPE ExceptionCatcherEnter(FunctionID, ObjectID) override;
    HRESULT STDMETHODCALLTYPE ExceptionCatcherLeave(void) override;
    HRESULT STDMETHODCALLTYPE COMClassicVTableCreated(ClassID, REFGUID, void*, ULONG) override;
    HRESULT STDMETHODCALLTYPE COMClassicVTableDestroyed(ClassID, REFGUID, void*) override;
    HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherFound(void) override;
    HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherExecute(void) override;
    HRESULT STDMETHODCALLTYPE ThreadNameChanged(ThreadID, ULONG, _In_reads_opt_(cchName) WCHAR[]) override;
    HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(int, BOOL[], COR_PRF_GC_REASON) override;
    HRESULT STDMETHODCALLTYPE SurvivingReferences(ULONG, ObjectID[], ULONG[]) override;
    HRESULT STDMETHODCALLTYPE GarbageCollectionFinished(void) override;
    HRESULT STDMETHODCALLTYPE FinalizeableObjectQueued(DWORD, ObjectID) override;
    HRESULT STDMETHODCALLTYPE RootReferences2(ULONG, ObjectID[], COR_PRF_GC_ROOT_KIND[], COR_PRF_GC_ROOT_FLAGS[], UINT_PTR[]) override;
    HRESULT STDMETHODCALLTYPE HandleCreated(GCHandleID, ObjectID) override;
    HRESULT STDMETHODCALLTYPE HandleDestroyed(GCHandleID) override;
    HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown*, void*, UINT) override;
    HRESULT STDMETHODCALLTYPE ProfilerAttachComplete(void) override;
    HRESULT STDMETHODCALLTYPE ProfilerDetachSucceeded(void) override;
    HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(FunctionID, ReJITID, BOOL) override;
    HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID, mdMethodDef, ICorProfilerFunctionControl*) override;
    HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(FunctionID, ReJITID, HRESULT, BOOL) override;
    HRESULT STDMETHODCALLTYPE ReJITError(ModuleID, mdMethodDef, FunctionID, HRESULT) override;
    HRESULT STDMETHODCALLTYPE MovedReferences2(ULONG, ObjectID[], ObjectID[], SIZE_T[]) override;
    HRESULT STDMETHODCALLTYPE SurvivingReferences2(ULONG, ObjectID[], SIZE_T[]) override;
    HRESULT STDMETHODCALLTYPE ConditionalWeakTableElementReferences(ULONG, ObjectID[], ObjectID[], GCHandleID[]) override;
    HRESULT STDMETHODCALLTYPE GetAssemblyReferences(const WCHAR*, ICorProfilerAssemblyReferenceProvider*) override;
    HRESULT STDMETHODCALLTYPE ModuleInMemorySymbolsUpdated(ModuleID) override;
    HRESULT STDMETHODCALLTYPE DynamicMethodJITCompilationStarted(FunctionID, BOOL, LPCBYTE, ULONG) override;
    HRESULT STDMETHODCALLTYPE DynamicMethodJITCompilationFinished(FunctionID, HRESULT, BOOL) override;
    HRESULT STDMETHODCALLTYPE DynamicMethodUnloaded(FunctionID) override;
    HRESULT STDMETHODCALLTYPE EventPipeEventDelivered(EVENTPIPE_PROVIDER, DWORD, DWORD, ULONG, LPCBYTE, ULONG, LPCBYTE, LPCGUID, LPCGUID, ThreadID, ULONG, UINT_PTR[]) override;
    HRESULT STDMETHODCALLTYPE EventPipeProviderCreated(EVENTPIPE_PROVIDER) override;

private:
    std::atomic<int> ref_count_;
};

}

#endif