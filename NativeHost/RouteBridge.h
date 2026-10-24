#pragma once
#include <windows.h>
#include <filesystem>
#include <string>
#include <stdexcept>
#include "vendor/json.hpp"

// PowerShell/NetTCPIP is started only for route requests, never a resident worker.
// Requests travel through stdin as JSON, never interpolated into command text.
inline nlohmann::json routeRequest(const nlohmann::json& request, const std::filesystem::path& app) {
    struct Scoped {
        HANDLE h=nullptr;
        ~Scoped(){if(h&&h!=INVALID_HANDLE_VALUE)CloseHandle(h);}
    } inputRead,inputWrite,outputRead,outputWrite,job,process,thread;
    auto check=[](bool ok,const char* text){if(!ok)throw std::runtime_error(text);};
    SECURITY_ATTRIBUTES sa{sizeof(sa),nullptr,TRUE};
    check(CreatePipe(&inputRead.h,&inputWrite.h,&sa,0),"Cannot create route input pipe.");
    check(CreatePipe(&outputRead.h,&outputWrite.h,&sa,0),"Cannot create route output pipe.");
    check(SetHandleInformation(inputWrite.h,HANDLE_FLAG_INHERIT,0)&&SetHandleInformation(outputRead.h,HANDLE_FLAG_INHERIT,0),"Cannot protect route pipes.");
    wchar_t system[MAX_PATH];check(GetSystemDirectoryW(system,MAX_PATH)!=0,"Cannot locate Windows PowerShell.");
    auto exe=std::filesystem::path(system)/L"WindowsPowerShell"/L"v1.0"/L"powershell.exe";
    auto script=app/L"CoreBin"/L"Routes.ps1";
    auto command=L"\""+exe.wstring()+L"\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+script.wstring()+L"\"";
    job.h=CreateJobObjectW(nullptr,nullptr);check(job.h!=nullptr,"Cannot create route worker job.");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    check(SetInformationJobObject(job.h,JobObjectExtendedLimitInformation,&limits,sizeof(limits)),"Cannot configure route worker job.");
    STARTUPINFOW start{};start.cb=sizeof(start);start.dwFlags=STARTF_USESTDHANDLES;
    start.hStdInput=inputRead.h;start.hStdOutput=outputWrite.h;start.hStdError=outputWrite.h;
    PROCESS_INFORMATION pi{};
    check(CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED,nullptr,app.c_str(),&start,&pi),"Cannot start route worker.");
    process.h=pi.hProcess;thread.h=pi.hThread;
    if(!AssignProcessToJobObject(job.h,process.h)){TerminateProcess(process.h,1);throw std::runtime_error("Cannot supervise route worker.");}
    check(ResumeThread(thread.h)!=static_cast<DWORD>(-1),"Cannot resume route worker.");
    CloseHandle(inputRead.h);inputRead.h=nullptr;CloseHandle(outputWrite.h);outputWrite.h=nullptr;
    auto text=request.dump()+"\n";DWORD written=0;
    check(WriteFile(inputWrite.h,text.data(),static_cast<DWORD>(text.size()),&written,nullptr)&&written==text.size(),"Cannot send route request.");
    CloseHandle(inputWrite.h);inputWrite.h=nullptr;
    std::string result;auto deadline=GetTickCount64()+60000;
    for(;;){
        DWORD available=0;
        if(PeekNamedPipe(outputRead.h,nullptr,0,nullptr,&available,nullptr)&&available){
            char buffer[8192];DWORD read=0;
            check(ReadFile(outputRead.h,buffer,(std::min)(available,static_cast<DWORD>(sizeof(buffer))),&read,nullptr),"Cannot read route response.");
            result.append(buffer,read);check(result.size()<4*1024*1024,"Route response is too large.");continue;
        }
        if(WaitForSingleObject(process.h,0)==WAIT_OBJECT_0)break;
        check(GetTickCount64()<deadline,"Route request timed out; refresh to verify Windows state.");Sleep(10);
    }
    DWORD exitCode=0;GetExitCodeProcess(process.h,&exitCode);
    check(exitCode==0&&!result.empty(),"Route worker failed; verify Windows NetTCPIP availability.");
    return nlohmann::json::parse(result);
}
