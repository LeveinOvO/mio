#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <sddl.h>
#include <aclapi.h>
#include <iphlpapi.h>
#include <initguid.h>
#include <netfw.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <chrono>
#include <algorithm>
#include <set>
#include <map>
#include <optional>
#include <sstream>
#include <iomanip>
#include "vendor/json.hpp"
#include "SqliteStore.h"
#include "NativeLogger.h"
#include "RouteBridge.h"

using json = nlohmann::json;
namespace fs = std::filesystem;
using namespace std::chrono_literals;

extern "C" INT WSAAPI GetAddrInfoExCancel(LPHANDLE nameHandle);
extern "C" INT WSAAPI GetAddrInfoExOverlappedResult(LPOVERLAPPED overlapped);

static std::string utf8(const std::wstring& text) {
    int n = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string value(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), value.data(), n, nullptr, nullptr);
    return value;
}
static std::wstring wide(const std::string& text) {
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (!n && !text.empty()) throw std::runtime_error("请求文本编码无效。");
    std::wstring value(n, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), value.data(), n);
    return value;
}
static void require(bool condition, const std::string& error) { if (!condition) throw std::runtime_error(error); }
static void hrcheck(HRESULT result, const std::string& stage) {
    if (FAILED(result)) {
        std::ostringstream message; message << stage << "（0x" << std::hex << static_cast<unsigned long>(result) << "）";
        throw std::runtime_error(message.str());
    }
}
class Handle {
    HANDLE value_ = nullptr;
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value_(other.value_) { other.value_ = nullptr; }
    Handle& operator=(Handle&& other) noexcept { if (this != &other) { reset(); value_ = other.value_; other.value_ = nullptr; } return *this; }
    HANDLE get() const { return value_; }
    explicit operator bool() const { return value_ && value_ != INVALID_HANDLE_VALUE; }
    void reset() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); value_ = nullptr; }
};
template<class T> class Com {
    T* value_ = nullptr;
public:
    ~Com() { if (value_) value_->Release(); }
    Com() = default; Com(const Com&) = delete; Com& operator=(const Com&) = delete;
    T** out() { return &value_; } T* get() const { return value_; } T* operator->() const { return value_; }
};
class Bstr {
    BSTR value_;
public:
    explicit Bstr(const std::string& text) : value_(SysAllocString(wide(text).c_str())) {}
    ~Bstr() { SysFreeString(value_); } operator BSTR() const { return value_; }
};
static std::string now() {
    SYSTEMTIME time{}; GetSystemTime(&time);
    char text[40]; snprintf(text, sizeof(text), "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ", time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
    return text;
}
static std::string guid() {
    GUID value{}; hrcheck(CoCreateGuid(&value), "无法生成规则标识");
    wchar_t text[40]; StringFromGUID2(value, text, 40);
    auto output = utf8(text); output = output.substr(1, output.size() - 2);
    std::transform(output.begin(), output.end(), output.begin(), [](unsigned char c) { return static_cast<char>(tolower(c)); });
    return output;
}
static std::string compact(std::string value) { value.erase(std::remove(value.begin(), value.end(), '-'), value.end()); return value; }
static std::string sidOf(HANDLE token) {
    DWORD size = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<unsigned char> buffer(size);
    require(GetTokenInformation(token, TokenUser, buffer.data(), size, &size), "无法读取用户身份。");
    LPWSTR sid = nullptr;
    require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sid), "无法读取用户身份。");
    auto result = utf8(sid); LocalFree(sid); return result;
}
static bool elevated(HANDLE token) { TOKEN_ELEVATION info{}; DWORD size = 0; return GetTokenInformation(token, TokenElevation, &info, sizeof(info), &size) && info.TokenIsElevated; }
static std::string currentSid() { HANDLE raw = nullptr; require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw), "无法读取用户身份。"); Handle token(raw); return sidOf(token.get()); }
static fs::path appDirectory() { std::wstring value(32768, L'\0'); DWORD size = GetModuleFileNameW(nullptr, value.data(), static_cast<DWORD>(value.size())); value.resize(size); return fs::path(value).parent_path(); }
[[maybe_unused]] static json readJson(const fs::path& file, json fallback) {
    if (!fs::exists(file)) return fallback;
    require(!(GetFileAttributesW(file.c_str()) & FILE_ATTRIBUTE_REPARSE_POINT), "配置文件不能是链接。");
    require(fs::file_size(file) <= 2 * 1024 * 1024, "配置文件过大。");
    std::ifstream stream(file, std::ios::binary); require(stream.good(), "无法读取配置文件。");
    return json::parse(stream);
}
static void writeJson(const fs::path& file, const json& value) {
    require(!fs::exists(file) || !(GetFileAttributesW(file.c_str()) & FILE_ATTRIBUTE_REPARSE_POINT), "配置文件不能是链接。");
    fs::path temp = file.parent_path() / (wide(guid()) + L".tmp");
    auto text = value.dump();
    Handle stream(CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    require(static_cast<bool>(stream), "无法保存配置。");
    DWORD written = 0;
    if (!WriteFile(stream.get(), text.data(), static_cast<DWORD>(text.size()), &written, nullptr) || written != text.size() || !FlushFileBuffers(stream.get())) {
        stream.reset(); DeleteFileW(temp.c_str()); throw std::runtime_error("配置写入失败。");
    }
    stream.reset();
    if (!MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temp.c_str()); throw std::runtime_error("无法提交配置。"); }
}
static void protectDirectory(const fs::path& directory, const std::string& userSid) {
    fs::create_directories(directory);
    require(!(GetFileAttributesW(directory.c_str()) & FILE_ATTRIBUTE_REPARSE_POINT), "配置目录不能是链接。");
    PSID owner = nullptr; PSECURITY_DESCRIPTOR previous = nullptr;
    require(GetNamedSecurityInfoW(const_cast<wchar_t*>(directory.c_str()), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &previous) == ERROR_SUCCESS, "无法检查配置权限。");
    SID_IDENTIFIER_AUTHORITY nt = SECURITY_NT_AUTHORITY;
    PSID admins = nullptr, system = nullptr, user = nullptr;
    require(AllocateAndInitializeSid(&nt, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &admins), "无法检查管理员身份。");
    require(AllocateAndInitializeSid(&nt, 1, SECURITY_LOCAL_SYSTEM_RID, 0, 0, 0, 0, 0, 0, 0, &system), "无法检查系统身份。");
    require(ConvertStringSidToSidW(wide(userSid).c_str(), &user), "无法检查当前用户身份。");
    bool safe = owner && (EqualSid(owner, admins) || EqualSid(owner, system) || EqualSid(owner, user));
    LocalFree(previous); FreeSid(admins); FreeSid(system); LocalFree(user);
    require(safe, "配置目录权限不正确，请由管理员检查。");
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    require(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)", SDDL_REVISION_1, &descriptor, nullptr), "无法设置配置权限。");
    BOOL present = FALSE, defaults = FALSE; PACL acl = nullptr; GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaults);
    DWORD result = SetNamedSecurityInfoW(const_cast<wchar_t*>(directory.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr);
    LocalFree(descriptor); require(result == ERROR_SUCCESS, "无法保护配置目录。");
}

struct Rule {
    std::string listen, target; int port = 0, targetPort = 0, protocol = 0;
    bool operator==(const Rule&) const = default;
    std::string listener() const { return listen + ":" + std::to_string(port); }
    std::string key() const { return (protocol == 0 ? "Tcp:" : "Udp:") + listener(); }
    json toJson() const { return {{"ListenAddress",listen},{"ListenPort",port},{"ConnectAddress",target},{"ConnectPort",targetPort},{"Protocol",protocol}}; }
};
static std::string ipv4(std::string text) {
    std::stringstream input(text); std::string part; int count = 0; std::string normalized;
    while (std::getline(input, part, '.')) {
        require(!part.empty() && part.size() <= 3 && std::all_of(part.begin(),part.end(),[](unsigned char c){return c>='0' && c<='9';}), "请输入完整 IPv4 地址。");
        int n = std::stoi(part); require(n <= 255, "IPv4 地址无效。");
        if (count++) normalized += '.'; normalized += std::to_string(n);
    }
    require(count == 4 && !text.empty() && text.back() != '.', "请输入完整 IPv4 地址。"); return normalized;
}
static std::string hostname(const std::string& text) {
    require(!text.empty() && text.size() <= 254, "请输入目标地址或域名。");
    try { return ipv4(text); } catch (...) {}
    require(text.find(':') == std::string::npos && text.find('/') == std::string::npos, "目标地址不能包含协议、路径或端口。");
    auto host = text; if (host.back() == '.') host.pop_back();
    std::stringstream stream(host); std::string label;
    while (std::getline(stream,label,'.'))
        require(!label.empty() && label.size() <= 63 && label.front() != '-' && label.back() != '-' &&
            std::all_of(label.begin(), label.end(), [](unsigned char c){return (c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='-';}), "域名格式无效。");
    require(!host.empty() && host.back() != '.' && !std::all_of(host.begin(),host.end(),[](unsigned char c){return (c>='0'&&c<='9')||c=='.';}), "域名格式无效。"); return text;
}
static Rule parseRule(const json& value) {
    Rule r{ipv4(value.at("ListenAddress").get<std::string>()), hostname(value.at("ConnectAddress").get<std::string>()), value.at("ListenPort").get<int>(), value.at("ConnectPort").get<int>(), value.value("Protocol",0)};
    require(r.port >= 1 && r.port <= 65535 && r.targetPort >= 1 && r.targetPort <= 65535, "端口必须是 1–65535 的整数。");
    require(r.protocol == 0 || r.protocol == 1, "请选择 TCP 或 UDP。"); return r;
}
static bool conflicts(const Rule& a, const Rule& b) { return a.protocol==b.protocol && a.port==b.port && (a.listen==b.listen || a.listen=="0.0.0.0" || b.listen=="0.0.0.0"); }
static bool isDomain(const std::string& text) { IN_ADDR value{}; return InetPtonW(AF_INET,wide(text).c_str(),&value)!=1; }
#include "ForwardSafety.h"
static std::string resolve(const std::string& target, const std::string& preferred) {
    if (!isDomain(target)) return ipv4(target);
    ADDRINFOEXW hints{}; hints.ai_family=AF_INET; hints.ai_socktype=SOCK_STREAM;
    ADDRINFOEXW* answers=nullptr;Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED operation{};operation.hEvent=event.get();HANDLE cancel=nullptr;
    auto query=wide(target);
    int error=GetAddrInfoExW(query.c_str(),nullptr,NS_ALL,nullptr,&hints,&answers,nullptr,&operation,nullptr,&cancel);
    if(error==WSA_IO_PENDING){
        if(WaitForSingleObject(event.get(),10000)!=WAIT_OBJECT_0){GetAddrInfoExCancel(&cancel);WaitForSingleObject(event.get(),INFINITE);if(answers)FreeAddrInfoExW(answers);throw std::runtime_error("DNS 查询超时，保留旧 IP。");}
        error=GetAddrInfoExOverlappedResult(&operation);
    }
    require(error==0,"域名解析失败（"+std::to_string(error)+"），保留旧 IP。");
    std::set<uint32_t> addresses;
    for(auto* a=answers;a;a=a->ai_next) if(a->ai_family==AF_INET) addresses.insert(ntohl(reinterpret_cast<sockaddr_in*>(a->ai_addr)->sin_addr.s_addr));
    FreeAddrInfoExW(answers); require(!addresses.empty(),"目标域名没有 IPv4 地址。");
    IN_ADDR previous{};
    if(InetPtonW(AF_INET,wide(preferred).c_str(),&previous)==1 && addresses.contains(ntohl(previous.s_addr)))return preferred;
    IN_ADDR selected{}; selected.s_addr=htonl(*addresses.begin()); wchar_t output[INET_ADDRSTRLEN]; InetNtopW(AF_INET,&selected,output,INET_ADDRSTRLEN); return utf8(output);
}
struct SocketRow { std::string address; int port; DWORD pid; };
static std::vector<SocketRow> sockets(int protocol) {
    DWORD size=0; DWORD result=protocol==0 ? GetExtendedTcpTable(nullptr,&size,FALSE,AF_INET,TCP_TABLE_OWNER_PID_LISTENER,0) : GetExtendedUdpTable(nullptr,&size,FALSE,AF_INET,UDP_TABLE_OWNER_PID,0);
    require(result==ERROR_INSUFFICIENT_BUFFER || result==NO_ERROR,"无法检查监听端口。");
    std::vector<unsigned char> buffer(size);
    result=protocol==0?GetExtendedTcpTable(buffer.data(),&size,FALSE,AF_INET,TCP_TABLE_OWNER_PID_LISTENER,0):GetExtendedUdpTable(buffer.data(),&size,FALSE,AF_INET,UDP_TABLE_OWNER_PID,0);
    require(result==NO_ERROR,"无法检查监听端口。"); std::vector<SocketRow> rows;
    auto address=[](DWORD value){IN_ADDR ip{};ip.s_addr=value;wchar_t out[INET_ADDRSTRLEN];InetNtopW(AF_INET,&ip,out,INET_ADDRSTRLEN);return utf8(out);};
    if(protocol==0){auto* t=reinterpret_cast<MIB_TCPTABLE_OWNER_PID*>(buffer.data());for(DWORD i=0;i<t->dwNumEntries;i++){auto& r=t->table[i];if(r.dwState==MIB_TCP_STATE_LISTEN)rows.push_back({address(r.dwLocalAddr),ntohs(static_cast<u_short>(r.dwLocalPort)),r.dwOwningPid});}}
    else {auto* t=reinterpret_cast<MIB_UDPTABLE_OWNER_PID*>(buffer.data());for(DWORD i=0;i<t->dwNumEntries;i++){auto& r=t->table[i];rows.push_back({address(r.dwLocalAddr),ntohs(static_cast<u_short>(r.dwLocalPort)),r.dwOwningPid});}}
    return rows;
}
static bool occupied(const Rule& rule){for(auto& r:sockets(rule.protocol))if(r.port==rule.port&&(r.address==rule.listen||r.address=="0.0.0.0"||rule.listen=="0.0.0.0"))return true;return false;}
static bool owns(const Rule& rule,DWORD pid){for(auto& r:sockets(rule.protocol))if(r.pid==pid&&r.port==rule.port&&r.address==rule.listen)return true;return false;}
static std::string hashFile(const fs::path& file) {
    BCRYPT_ALG_HANDLE algorithm=nullptr; BCRYPT_HASH_HANDLE hash=nullptr;
    require(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)==0,"无法校验 Realm。");
    DWORD length=0,size=0; BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&length),sizeof(length),&size,0);
    std::vector<unsigned char> object(length); std::ifstream input(file,std::ios::binary);
    if(BCryptCreateHash(algorithm,&hash,object.data(),length,nullptr,0,0)!=0){BCryptCloseAlgorithmProvider(algorithm,0);throw std::runtime_error("无法校验 Realm。");}
    std::vector<unsigned char> block(16384); while(input){input.read(reinterpret_cast<char*>(block.data()),block.size());auto n=input.gcount();if(n>0)BCryptHashData(hash,block.data(),static_cast<ULONG>(n),0);}
    unsigned char output[32];auto result=BCryptFinishHash(hash,output,32,0);BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);require(result==0,"无法校验 Realm。");
    std::ostringstream text;for(auto b:output)text<<std::hex<<std::setw(2)<<std::setfill('0')<<static_cast<int>(b);return text.str();
}
struct CoreProcess { Handle process,job; DWORD pid=0; fs::path config; bool running() const{return process&&WaitForSingleObject(process.get(),0)==WAIT_TIMEOUT;} };
static void stopCore(CoreProcess& core){core.job.reset();if(core.process)WaitForSingleObject(core.process.get(),10000);core.process.reset();if(!core.config.empty())DeleteFileW(core.config.c_str());}
static CoreProcess startCore(const Rule& rule,const fs::path& executable,const fs::path& directory){
    checkRealmRuntime();
    validateForwardGraph({rule},localIPv4Addresses());
    require(!occupied(rule),"监听地址和端口已被其他程序占用。");
    require(hashFile(executable)=="7c7030c8e40a14dbd11263c22932171ed2170c9e349b76de9351c495f698af6c","Realm 核心校验失败。");
    CoreProcess result;result.config=directory/(wide("realm-"+guid())+L".json");
    writeJson(result.config,{{"log",{{"level","off"}}},{"network",{{"no_tcp",rule.protocol==1},{"use_udp",rule.protocol==1},{"udp_timeout",30}}},{"endpoints",json::array({{{"listen",rule.listener()},{"remote",rule.target+":"+std::to_string(rule.targetPort)}}})}});
    result.job=Handle(CreateJobObjectW(nullptr,nullptr));require(static_cast<bool>(result.job),"无法创建核心进程管理对象。");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    require(SetInformationJobObject(result.job.get(),JobObjectExtendedLimitInformation,&limits,sizeof(limits)),"无法设置核心退出清理。");
    std::wstring command=L"\""+executable.wstring()+L"\" -c \""+result.config.wstring()+L"\"";
    // A suspended process is assigned to the job before it can run, closing the crash/orphan race.
    STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};
    LPWCH inherited=GetEnvironmentStringsW();std::vector<wchar_t> environment;
    for(auto* item=inherited;item&&*item;item+=wcslen(item)+1){if(_wcsnicmp(item,L"REALM_CONF=",11)==0)continue;environment.insert(environment.end(),item,item+wcslen(item)+1);}
    if(inherited)FreeEnvironmentStringsW(inherited);environment.push_back(L'\0');
    if(!CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW|CREATE_SUSPENDED|CREATE_UNICODE_ENVIRONMENT,environment.data(),executable.parent_path().c_str(),&startup,&process)){
        DeleteFileW(result.config.c_str());throw std::runtime_error("无法启动 Realm。");}
    result.process=Handle(process.hProcess);Handle thread(process.hThread);result.pid=process.dwProcessId;
    try{
        require(AssignProcessToJobObject(result.job.get(),result.process.get()),"无法管理 Realm 进程。");
        ResumeThread(thread.get());
        for(int i=0;i<30;i++){require(result.running(),"Realm 启动后退出。");if(owns(rule,result.pid))return result;Sleep(100);}
        throw std::runtime_error("Realm 未建立监听端口。");
    }catch(...){TerminateProcess(result.process.get(),1);stopCore(result);throw;}
}

static const std::string FirewallGroup="Mio.Managed.TCP.v1";
static std::string firewallName(const json& entry){return "Mio.TCP."+compact(entry.at("Id").get<std::string>());}
static std::string bstrText(BSTR value){return value?utf8(std::wstring(value,SysStringLen(value))):"";}
static json firewallSnapshot(INetFwRule* rule){
    json output;BSTR text=nullptr;long number=0;VARIANT_BOOL flag=VARIANT_FALSE;
    auto getText=[&](const char* key,HRESULT(INetFwRule::*getter)(BSTR*)){text=nullptr;hrcheck((rule->*getter)(&text),"读取防火墙规则失败");output[key]=bstrText(text);SysFreeString(text);};
    auto getNumber=[&](const char* key,HRESULT(INetFwRule::*getter)(long*)){hrcheck((rule->*getter)(&number),"读取防火墙规则失败");output[key]=number;};
    getText("Name",&INetFwRule::get_Name);getText("Group",&INetFwRule::get_Grouping);getText("Description",&INetFwRule::get_Description);
    getNumber("Protocol",&INetFwRule::get_Protocol);NET_FW_RULE_DIRECTION direction;hrcheck(rule->get_Direction(&direction),"读取防火墙方向失败");output["Direction"]=direction;
    NET_FW_ACTION action;hrcheck(rule->get_Action(&action),"读取防火墙动作失败");output["Action"]=action;
    hrcheck(rule->get_Enabled(&flag),"读取防火墙状态失败");output["Enabled"]=flag!=VARIANT_FALSE;
    getText("LocalAddresses",&INetFwRule::get_LocalAddresses);getText("LocalPorts",&INetFwRule::get_LocalPorts);getText("RemoteAddresses",&INetFwRule::get_RemoteAddresses);getText("RemotePorts",&INetFwRule::get_RemotePorts);
    // Match the automation API used by existing configuration snapshots.
    LPOLESTR profileName=const_cast<LPOLESTR>(L"Profiles");DISPID profileId;
    hrcheck(rule->GetIDsOfNames(IID_NULL,&profileName,1,LOCALE_USER_DEFAULT,&profileId),"读取防火墙网络类型失败");
    DISPPARAMS noArguments{};VARIANT profileValue;VariantInit(&profileValue);
    hrcheck(rule->Invoke(profileId,IID_NULL,LOCALE_USER_DEFAULT,DISPATCH_PROPERTYGET,&noArguments,&profileValue,nullptr,nullptr),"读取防火墙网络类型失败");
    VARIANT profileLong;VariantInit(&profileLong);hrcheck(VariantChangeType(&profileLong,&profileValue,0,VT_I4),"读取防火墙网络类型失败");output["Profiles"]=profileLong.lVal;VariantClear(&profileValue);VariantClear(&profileLong);
    hrcheck(rule->get_EdgeTraversal(&flag),"读取防火墙状态失败");output["EdgeTraversal"]=flag!=VARIANT_FALSE;
    getText("ApplicationName",&INetFwRule::get_ApplicationName);getText("ServiceName",&INetFwRule::get_ServiceName);getText("InterfaceTypes",&INetFwRule::get_InterfaceTypes);
    VARIANT interfaces;VariantInit(&interfaces);hrcheck(rule->get_Interfaces(&interfaces),"读取防火墙接口失败");
    std::vector<std::string> interfaceNames;
    if((interfaces.vt&VT_ARRAY)&&interfaces.parray){LONG first=0,last=-1;SafeArrayGetLBound(interfaces.parray,1,&first);SafeArrayGetUBound(interfaces.parray,1,&last);for(LONG i=first;i<=last;i++){BSTR name=nullptr;if(SUCCEEDED(SafeArrayGetElement(interfaces.parray,&i,&name))){interfaceNames.push_back(bstrText(name));SysFreeString(name);}}}
    else require(interfaces.vt==VT_EMPTY||interfaces.vt==VT_NULL,"防火墙接口信息无效。");
    VariantClear(&interfaces);std::sort(interfaceNames.begin(),interfaceNames.end());std::string joined;for(auto& name:interfaceNames){if(!joined.empty())joined+=',';joined+=name;}
    output["Interfaces"]=joined;output["IcmpTypesAndCodes"]="";return output;
}
static void fwPolicy(Com<INetFwPolicy2>& policy,Com<INetFwRules>& rules){hrcheck(CoCreateInstance(CLSID_NetFwPolicy2,nullptr,CLSCTX_INPROC_SERVER,IID_INetFwPolicy2,reinterpret_cast<void**>(policy.out())),"无法访问 Windows 防火墙");hrcheck(policy->get_Rules(rules.out()),"无法读取防火墙规则");}
static std::vector<json> findFirewall(const std::string& name){
    Com<INetFwPolicy2> policy;Com<INetFwRules> rules;fwPolicy(policy,rules);Com<IUnknown> unknown;hrcheck(rules->get__NewEnum(unknown.out()),"无法枚举防火墙规则");Com<IEnumVARIANT> iterator;hrcheck(unknown->QueryInterface(IID_IEnumVARIANT,reinterpret_cast<void**>(iterator.out())),"无法枚举防火墙规则");
    std::vector<json> found;VARIANT item;VariantInit(&item);ULONG count=0;
    while(iterator->Next(1,&item,&count)==S_OK){
        if(item.vt==VT_DISPATCH){Com<INetFwRule> rule;if(SUCCEEDED(item.pdispVal->QueryInterface(IID_INetFwRule,reinterpret_cast<void**>(rule.out())))){
            BSTR ruleName=nullptr;hrcheck(rule->get_Name(&ruleName),"读取防火墙名称失败");auto value=bstrText(ruleName);SysFreeString(ruleName);if(value==name)found.push_back(firewallSnapshot(rule.get()));}}
        VariantClear(&item);VariantInit(&item);
    }return found;
}
static void checkOwned(const json& entry){auto actual=findFirewall(firewallName(entry));if(actual.empty())return;require(actual.size()==1&&!entry.at("Expected").is_null()&&actual[0]==entry.at("Expected")&&actual[0].at("Group")==FirewallGroup,"防火墙规则已被外部修改或出现同名冲突，请检查 Windows 防火墙。");}
static json normalizeScope(json scope){
    scope["ListenAddress"]=ipv4(scope.at("ListenAddress").get<std::string>());int port=scope.at("ListenPort").get<int>();require(port>=1&&port<=65535,"防火墙端口无效。");
    int protocol=scope.value("Protocol",0),profiles=scope.at("Profiles").get<int>(),mode=scope.at("SourceMode").get<int>();require((protocol==0||protocol==1)&&profiles>0&&(profiles&~7)==0&&(mode==0||mode==1),"防火墙配置无效。");
    std::set<std::string> cidrs;
    if(mode==0){require(scope.at("SourceCidrs").is_array()&&scope.at("SourceCidrs").size()>0&&scope.at("SourceCidrs").size()<=100,"来源 CIDR 必须填写，最多 100 项。");
        for(auto& item:scope.at("SourceCidrs")){auto text=item.get<std::string>();auto slash=text.find('/');require(slash!=std::string::npos,"请输入 IPv4 CIDR。");auto ip=ipv4(text.substr(0,slash));auto prefixText=text.substr(slash+1);require(!prefixText.empty()&&std::all_of(prefixText.begin(),prefixText.end(),[](unsigned char c){return c>='0'&&c<='9';}),"CIDR 前缀无效。");int prefix=std::stoi(prefixText);require(prefix>=1&&prefix<=32,"CIDR 前缀应为 1–32。");IN_ADDR value{};InetPtonW(AF_INET,wide(ip).c_str(),&value);value.s_addr=htonl(ntohl(value.s_addr)&(0xffffffffu<<(32-prefix)));wchar_t output[INET_ADDRSTRLEN];InetNtopW(AF_INET,&value,output,INET_ADDRSTRLEN);cidrs.insert(utf8(output)+"/"+std::to_string(prefix));}}
    return {{"ListenAddress",scope["ListenAddress"]},{"ListenPort",port},{"SourceMode",mode},{"SourceCidrs",std::vector<std::string>(cidrs.begin(),cidrs.end())},{"Profiles",profiles},{"Protocol",protocol}};
}
[[maybe_unused]] static void verifyFilePermissions(const fs::path& path){
    if(!fs::exists(path))return;
    require(!(GetFileAttributesW(path.c_str())&FILE_ATTRIBUTE_REPARSE_POINT),"配置文件不能是链接。");
    PSID owner=nullptr;PACL acl=nullptr;PSECURITY_DESCRIPTOR descriptor=nullptr;
    require(GetNamedSecurityInfoW(const_cast<wchar_t*>(path.c_str()),SE_FILE_OBJECT,OWNER_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION,&owner,nullptr,&acl,nullptr,&descriptor)==ERROR_SUCCESS,"无法检查配置文件权限。");
    SID_IDENTIFIER_AUTHORITY nt=SECURITY_NT_AUTHORITY;PSID admins=nullptr,system=nullptr;
    AllocateAndInitializeSid(&nt,2,SECURITY_BUILTIN_DOMAIN_RID,DOMAIN_ALIAS_RID_ADMINS,0,0,0,0,0,0,&admins);
    AllocateAndInitializeSid(&nt,1,SECURITY_LOCAL_SYSTEM_RID,0,0,0,0,0,0,0,&system);
    bool safe=owner&&(EqualSid(owner,admins)||EqualSid(owner,system))&&acl;
    if(acl)for(DWORD i=0;i<acl->AceCount;i++){void* raw=nullptr;if(!GetAce(acl,i,&raw)){safe=false;break;}auto* header=static_cast<ACE_HEADER*>(raw);
        if(header->AceType==ACCESS_ALLOWED_ACE_TYPE){auto* ace=static_cast<ACCESS_ALLOWED_ACE*>(raw);auto* sid=&ace->SidStart;
            DWORD mutating=FILE_WRITE_DATA|FILE_APPEND_DATA|FILE_WRITE_EA|FILE_WRITE_ATTRIBUTES|DELETE|WRITE_DAC|WRITE_OWNER|GENERIC_WRITE|GENERIC_ALL;
            if((ace->Mask&mutating)&&!EqualSid(sid,admins)&&!EqualSid(sid,system))safe=false;}}
    FreeSid(admins);FreeSid(system);LocalFree(descriptor);require(safe,"配置文件权限不正确，请由管理员修复。");
}
static void putProfiles(INetFwRule* rule,long value){
    LPOLESTR name=const_cast<LPOLESTR>(L"Profiles");DISPID id;hrcheck(rule->GetIDsOfNames(IID_NULL,&name,1,LOCALE_USER_DEFAULT,&id),"防火墙网络类型设置失败");
    VARIANT argument;VariantInit(&argument);argument.vt=VT_I4;argument.lVal=value;DISPID put=DISPID_PROPERTYPUT;DISPPARAMS params{&argument,&put,1,1};
    hrcheck(rule->Invoke(id,IID_NULL,LOCALE_USER_DEFAULT,DISPATCH_PROPERTYPUT,&params,nullptr,nullptr,nullptr),"防火墙网络类型设置失败");
}
static std::string scopeKey(const json& scope){return (scope.value("Protocol",0)==0?"Tcp:":"Udp:")+scope.at("ListenAddress").get<std::string>()+":"+std::to_string(scope.at("ListenPort").get<int>());}
static json applyFirewall(json entry,bool enabled){
    for(const auto* serviceName:{L"BFE",L"MpsSvc"}){
        SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);require(manager!=nullptr,"无法检查防火墙服务。");
        SC_HANDLE service=OpenServiceW(manager,serviceName,SERVICE_QUERY_STATUS);SERVICE_STATUS status{};
        bool running=service&&QueryServiceStatus(service,&status)&&status.dwCurrentState==SERVICE_RUNNING;
        if(service)CloseServiceHandle(service);CloseServiceHandle(manager);
        require(running,"Windows 防火墙服务 BFE/MpsSvc 未运行，未修改防火墙。");
    }
    checkOwned(entry);auto scope=normalizeScope(entry.at("Scope"));Com<INetFwPolicy2> policy;Com<INetFwRules> rules;fwPolicy(policy,rules);Com<INetFwRule> rule;auto name=firewallName(entry);bool exists=!findFirewall(name).empty();
    if(exists)hrcheck(rules->Item(Bstr(name),rule.out()),"无法读取防火墙规则");else hrcheck(CoCreateInstance(CLSID_NetFwRule,nullptr,CLSCTX_INPROC_SERVER,IID_INetFwRule,reinterpret_cast<void**>(rule.out())),"无法创建防火墙规则");
    hrcheck(rule->put_Enabled(VARIANT_FALSE),"无法停用防火墙规则");
    hrcheck(rule->put_Name(Bstr(name)),"防火墙名称设置失败");hrcheck(rule->put_Grouping(Bstr(FirewallGroup)),"防火墙分组设置失败");hrcheck(rule->put_Description(Bstr("Mio 入站规则。")),"防火墙描述设置失败");
    hrcheck(rule->put_Protocol(scope.value("Protocol",0)==0?6:17),"防火墙协议设置失败");hrcheck(rule->put_Direction(NET_FW_RULE_DIR_IN),"防火墙方向设置失败");hrcheck(rule->put_Action(NET_FW_ACTION_ALLOW),"防火墙动作设置失败");
    auto listen=scope.at("ListenAddress").get<std::string>();hrcheck(rule->put_LocalAddresses(Bstr(listen=="0.0.0.0"?"*":listen)),"防火墙本地地址设置失败");hrcheck(rule->put_LocalPorts(Bstr(std::to_string(scope.at("ListenPort").get<int>()))),"防火墙本地端口设置失败");
    std::string remote="*";if(scope.at("SourceMode")==0){remote="";for(auto& cidr:scope.at("SourceCidrs")){if(!remote.empty())remote+=',';remote+=cidr.get<std::string>();}}
    hrcheck(rule->put_RemoteAddresses(Bstr(remote)),"防火墙来源设置失败");hrcheck(rule->put_RemotePorts(Bstr("*")),"防火墙远程端口设置失败");putProfiles(rule.get(),scope.at("Profiles").get<int>());hrcheck(rule->put_EdgeTraversal(VARIANT_FALSE),"防火墙边缘遍历设置失败");hrcheck(rule->put_InterfaceTypes(Bstr("All")),"防火墙接口设置失败");
    if(!exists)hrcheck(rules->Add(rule.get()),"防火墙规则创建失败");hrcheck(rule->put_Enabled(enabled?VARIANT_TRUE:VARIANT_FALSE),"防火墙启用失败");
    entry["Scope"]=scope;entry["Expected"]=firewallSnapshot(rule.get());entry["Message"]=enabled?"防火墙规则已启用":"防火墙规则已禁用";return entry;
}
static void removeFirewall(const json& entry){checkOwned(entry);auto name=firewallName(entry);if(findFirewall(name).empty())return;Com<INetFwPolicy2> policy;Com<INetFwRules> rules;fwPolicy(policy,rules);hrcheck(rules->Remove(Bstr(name)),"防火墙规则删除失败");require(findFirewall(name).empty(),"防火墙规则删除后仍存在。");}

class Host {
    fs::path directory_,executable_;std::unique_ptr<SqliteStore> store_;std::unique_ptr<NativeLogger> logger_;json config_,firewalls_;std::map<std::string,CoreProcess> cores_;std::map<std::string,std::string> errors_;
    std::mutex mutex_;std::atomic<bool> stopping_{false};std::string sid_;std::wstring pipeName_;std::thread server_,monitor_;
    std::condition_variable wake_;std::mutex wakeMutex_;bool requested_=false;
    bool headless_;HWND window_=nullptr;NOTIFYICONDATAW tray_{};bool trayAdded_=false;UINT taskbarCreated_=0;UINT openWindowMessage_=0;
    Handle gui_;DWORD guiPid_=0;
    json monitorStatus_={{"DomainCount",0},{"PausedCount",0},{"Checking",false},{"Error",nullptr},{"DnsErrorCount",0}};
    void saveRules(){store_->replaceRules(config_.at("Rules"));}
    void saveFirewalls(){store_->replaceFirewalls(firewalls_.at("Rules"));}
    void reloadStore(){config_={{"Version",1},{"Rules",store_->readRules()}};firewalls_={{"Version",1},{"Rules",store_->readFirewalls()}};validateConfig();}
    static int findRule(const json& rules,const Rule& applied){for(size_t i=0;i<rules.size();i++)if(parseRule(rules[i].at("Applied"))==applied)return static_cast<int>(i);return -1;}
    static int findFirewallIndex(const json& rules,const std::string& key){for(size_t i=0;i<rules.size();i++)if(scopeKey(rules[i].at("Scope"))==key)return static_cast<int>(i);return -1;}
    void restart(const std::string& id,const Rule& next,const std::optional<Rule>& previous){
        checkForwardSafety(id,next);
        bool had=cores_.contains(id);if(had){stopCore(cores_.at(id));cores_.erase(id);}
        try{cores_.emplace(id,startCore(next,executable_,directory_));errors_.erase(id);if(logger_)logger_->info("realm.start","转发核心已启动",id);}
        catch(const std::exception& error){errors_[id]=error.what();if(logger_)logger_->error("realm.start.failed",error.what(),id);if(had&&previous){try{cores_.emplace(id,startCore(*previous,executable_,directory_));}catch(const std::exception& e){errors_[id]=e.what();}}throw;}
    }
    void checkForwardSafety(const std::string& id,const Rule& next){
        std::vector<Rule> rules{next};
        for(auto& row:config_["Rules"])if(row["Id"]!=id&&row["Enabled"].get<bool>())rules.push_back(parseRule(row["Applied"]));
        validateForwardGraph(rules,localIPv4Addresses());
    }
    void validateConfig(){
        require(config_.value("Version",0)==1&&config_.at("Rules").is_array()&&config_.at("Rules").size()<=1000,"规则配置无效。");std::set<std::string> ids,keys;
        for(auto& entry:config_.at("Rules")){auto id=entry.at("Id").get<std::string>();GUID parsed{};require(SUCCEEDED(CLSIDFromString(wide("{"+id+"}").c_str(),&parsed)),"规则标识无效。");auto desired=parseRule(entry.at("Desired")),applied=parseRule(entry.at("Applied"));ipv4(applied.target);require(desired.key()==applied.key()&&desired.targetPort==applied.targetPort,"目标配置不一致。");require(ids.insert(id).second&&keys.insert(desired.key()).second,"规则配置重复。");entry.at("Enabled").get<bool>();}
        require(firewalls_.value("Version",0)==1&&firewalls_.at("Rules").is_array()&&firewalls_.at("Rules").size()<=1000,"防火墙配置无效。");
        for(auto& entry:firewalls_.at("Rules")){GUID id{};require(SUCCEEDED(CLSIDFromString(wide("{"+entry.at("Id").get<std::string>()+"}").c_str(),&id)),"防火墙标识无效。");normalizeScope(entry.at("Scope"));}
    }
    void checkSnapshot(const json& current,const json& expected){
        if(expected.is_null())return;
        require(current.at("Id")==expected.at("Id")&&current.at("Enabled")==expected.at("Enabled")&&parseRule(current.at("Desired"))==parseRule(expected.at("Desired"))&&parseRule(current.at("Applied"))==parseRule(expected.at("Applied")),"规则已变化，请刷新后重试。");
    }
    void operate(const json& request){
        auto op=request.at("Operation").get<std::string>();require(op=="Add"||op=="Edit"||op=="Delete"||op=="Enable"||op=="Disable","不支持此操作。");
        auto original=request.value("Original",json(nullptr)),updated=request.value("Updated",json(nullptr));
        int index=op=="Add"?-1:findRule(config_.at("Rules"),parseRule(original));require(op=="Add"||index>=0,"转发已变化，请刷新后重试。");
        json old=index<0?json(nullptr):config_["Rules"][index];if(index>=0)checkSnapshot(old,request.value("Expected",json(nullptr)));
        bool enabled=op=="Add"||op=="Enable"||(op=="Edit"&&old.at("Enabled").get<bool>());
        Rule desired=op=="Add"||op=="Edit"?parseRule(updated):parseRule(old.at("Desired"));
        std::string id=index<0?guid():old.at("Id").get<std::string>();
        if(op!="Delete"&&op!="Disable")for(auto& entry:config_["Rules"]){if(entry["Id"]==id)continue;auto other=parseRule(entry["Desired"]);require(other.key()!=desired.key()&&!(enabled&&entry["Enabled"].get<bool>()&&conflicts(other,desired)),"监听地址、端口和协议与现有规则冲突。");}
        int fw=index<0?-1:findFirewallIndex(firewalls_["Rules"],parseRule(old["Applied"]).key());
        if(fw>=0){checkOwned(firewalls_["Rules"][fw]);if(op=="Edit"){int other=findFirewallIndex(firewalls_["Rules"],desired.key());require(other<0||other==fw,"新协议或端口已有防火墙配置。");}}
        Rule applied=desired;
        if(op=="Disable")applied=parseRule(old["Applied"]);
        if(op!="Delete"&&op!="Disable"&&enabled)applied.target=resolve(desired.target,index<0?"":parseRule(old["Applied"]).target);
        else if(op=="Edit"&&!enabled)applied.target=isDomain(desired.target)?parseRule(old["Applied"]).target:desired.target;
        if(enabled&&op!="Delete"&&op!="Disable"){checkRealmRuntime();checkForwardSafety(id,applied);}
        json oldFirewall=fw>=0?firewalls_["Rules"][fw]:json(nullptr);
        try{
            if(fw>=0&&(op=="Delete"||op=="Disable"||(op=="Edit"&&parseRule(old["Applied"]).key()!=desired.key()))){
                firewalls_["Rules"][fw]=applyFirewall(firewalls_["Rules"][fw],false);saveFirewalls();}
            if(op=="Delete"||op=="Disable"){if(cores_.contains(id)){stopCore(cores_.at(id));cores_.erase(id);}}
            else if(enabled)restart(id,applied,index<0?std::nullopt:std::optional(parseRule(old["Applied"])));
            if(op=="Delete"){config_["Rules"].erase(index);errors_.erase(id);}
            else{json entry={{"Id",id},{"Desired",desired.toJson()},{"Applied",applied.toJson()},{"Enabled",op=="Disable"?false:enabled},{"Message",op=="Disable"?"转发已停用":"转发配置已保存"},{"LastChecked",now()}};if(index<0)config_["Rules"].push_back(entry);else config_["Rules"][index]=entry;}
            saveRules();
        }catch(...){
            if(index<0){auto& rows=config_["Rules"];for(size_t i=0;i<rows.size();i++)if(rows[i]["Id"]==id){rows.erase(i);break;}}
            else{int pos=-1;for(size_t i=0;i<config_["Rules"].size();i++)if(config_["Rules"][i]["Id"]==id)pos=static_cast<int>(i);if(pos<0)config_["Rules"].push_back(old);else config_["Rules"][pos]=old;}
            if(cores_.contains(id)){stopCore(cores_.at(id));cores_.erase(id);}
            if(!old.is_null()&&old["Enabled"].get<bool>()){try{cores_.emplace(id,startCore(parseRule(old["Applied"]),executable_,directory_));}catch(const std::exception& e){errors_[id]=e.what();}}
            if(fw>=0)try{
                auto restore=firewalls_["Rules"][fw];restore["Scope"]=oldFirewall["Scope"];
                firewalls_["Rules"][fw]=applyFirewall(restore,oldFirewall["Expected"].is_object()&&oldFirewall["Expected"].value("Enabled",false));saveFirewalls();
            }catch(const std::exception& e){logger_->error("firewall.rollback.failed",e.what(),id);throw std::runtime_error(std::string("转发操作失败，防火墙恢复也失败，请刷新核验：")+e.what());}
            throw;
        }
        if(fw>=0)try{
            if(op=="Delete"){removeFirewall(firewalls_["Rules"][fw]);firewalls_["Rules"].erase(fw);}
            else{auto scope=firewalls_["Rules"][fw]["Scope"];scope["ListenAddress"]=desired.listen;scope["ListenPort"]=desired.port;scope["Protocol"]=desired.protocol;firewalls_["Rules"][fw]["Scope"]=scope;firewalls_["Rules"][fw]=applyFirewall(firewalls_["Rules"][fw],op!="Disable"&&enabled);}
            saveFirewalls();
        }catch(const std::exception& e){logger_->error("firewall.sync.partial",e.what(),id);throw std::runtime_error(std::string("转发已更新，但防火墙同步失败（可能已部分修改），请刷新检查：")+e.what());}
    }
    void configureFirewall(const json& request){
        auto expected=request.at("Rule");int index=findRule(config_["Rules"],parseRule(expected.at("Applied")));require(index>=0,"转发规则已变化。");checkSnapshot(config_["Rules"][index],expected);
        auto scope=normalizeScope(request.at("Scope"));require(scopeKey(scope)==parseRule(expected["Applied"]).key(),"防火墙范围与转发规则不一致。");
        int pos=findFirewallIndex(firewalls_["Rules"],scopeKey(scope));auto old=pos<0?json(nullptr):firewalls_["Rules"][pos];
        auto expectedFw=request.value("Expected",json(nullptr));if(!old.is_null()){auto comparable=old;comparable["Scope"]=normalizeScope(old["Scope"]);auto incoming=expectedFw;if(!incoming.is_null())incoming["Scope"]=normalizeScope(incoming["Scope"]);require(comparable==incoming,"防火墙设置已变化，请重新打开。");checkOwned(old);}else require(expectedFw.is_null(),"防火墙设置已变化。");
        auto entry=old.is_null()?json{{"Id",guid()},{"Scope",scope},{"Expected",nullptr},{"Message",""}}:old;entry["Scope"]=scope;
        if(pos<0){firewalls_["Rules"].push_back(entry);pos=static_cast<int>(firewalls_["Rules"].size()-1);}else firewalls_["Rules"][pos]=entry;saveFirewalls();
        try{firewalls_["Rules"][pos]=applyFirewall(entry,config_["Rules"][index]["Enabled"].get<bool>());saveFirewalls();}
        catch(...){if(old.is_null()&&findFirewall(firewallName(entry)).empty()){firewalls_["Rules"].erase(pos);saveFirewalls();}throw;}
    }
    void deleteFirewall(const json& request){auto expected=request.at("Expected");int pos=-1;for(size_t i=0;i<firewalls_["Rules"].size();i++)if(firewalls_["Rules"][i]["Id"]==expected["Id"])pos=static_cast<int>(i);require(pos>=0,"防火墙设置已变化。");auto old=firewalls_["Rules"][pos];auto compare=expected;compare["Scope"]=normalizeScope(compare["Scope"]);old["Scope"]=normalizeScope(old["Scope"]);require(old==compare,"防火墙设置已变化。");removeFirewall(old);firewalls_["Rules"].erase(pos);saveFirewalls();}
    json state(){
        json entries=json::array();for(auto& entry:config_["Rules"]){auto id=entry["Id"].get<std::string>();entries.push_back({{"Rule",entry},{"Running",cores_.contains(id)&&cores_.at(id).running()},{"Error",errors_.contains(id)?errors_[id]:""}});}
        json actual=json::object();std::string fwError;
        try{for(auto& entry:firewalls_["Rules"])actual[firewallName(entry)]=findFirewall(firewallName(entry));}catch(const std::exception& e){fwError=e.what();}
        try{checkRealmRuntime();}catch(const std::exception& e){monitorStatus_["Error"]=e.what();}
        return {{"Rules",entries},{"Monitor",monitorStatus_},{"FirewallConfig",firewalls_},{"FirewallActual",actual},{"FirewallError",fwError}};
    }
    void tick(){
        std::lock_guard lock(mutex_);if(stopping_)return;auto baseline=config_;bool changed=false;int domains=0,dnsErrors=0;
        for(auto& entry:config_["Rules"]){if(!entry["Enabled"].get<bool>())continue;auto id=entry["Id"].get<std::string>();auto applied=parseRule(entry["Applied"]);auto desired=parseRule(entry["Desired"]);
            try{checkForwardSafety(id,applied);}catch(const std::exception& e){if(cores_.contains(id)){stopCore(cores_.at(id));cores_.erase(id);}errors_[id]=e.what();logger_->error("forward.safety.blocked",e.what(),id);continue;}
            if(!cores_.contains(id)||!cores_.at(id).running()){if(cores_.contains(id)){stopCore(cores_.at(id));cores_.erase(id);}try{cores_.emplace(id,startCore(applied,executable_,directory_));errors_.erase(id);if(logger_)logger_->info("realm.monitor.start","转发核心已恢复",id);}catch(const std::exception& e){errors_[id]=e.what();if(logger_)logger_->error("realm.monitor.start.failed",e.what(),id);}}
            if(isDomain(desired.target)){domains++;try{auto ip=resolve(desired.target,applied.target);if(ip!=applied.target){auto next=applied;next.target=ip;restart(id,next,applied);entry["Applied"]=next.toJson();entry["Message"]="目标 IP 已更新";if(logger_)logger_->info("dns.changed","目标 IP 已更新",id);}else entry["Message"]="解析正常，IP 未变化";entry["LastChecked"]=now();changed=true;}catch(const std::exception& e){entry["Message"]=std::string("解析失败，保留旧 IP：")+e.what();entry["LastChecked"]=now();dnsErrors++;changed=true;if(logger_)logger_->warning("dns.check.failed",e.what(),id);}}
        }
        monitorStatus_={{"DomainCount",domains},{"PausedCount",0},{"Checking",false},{"Error",nullptr},{"DnsErrorCount",dnsErrors}};
        if(changed)try{saveRules();}catch(const std::exception& e){
            monitorStatus_["Error"]=e.what();if(logger_)logger_->error("database.save.failed",e.what());for(auto& old:baseline["Rules"]){auto id=old["Id"].get<std::string>();for(auto& entry:config_["Rules"])if(entry["Id"]==id&&parseRule(entry["Applied"])!=parseRule(old["Applied"])){ 
                try{restart(id,parseRule(old["Applied"]),parseRule(entry["Applied"]));}catch(const std::exception& error){errors_[id]=error.what();}}}
            config_=baseline;}
        if(window_)PostMessageW(window_,WM_APP+3,0,0);
    }
    bool authenticate(HANDLE pipe){
        if(!ImpersonateNamedPipeClient(pipe))return false;HANDLE raw=nullptr;bool ok=OpenThreadToken(GetCurrentThread(),TOKEN_QUERY,TRUE,&raw);RevertToSelf();if(!ok)return false;Handle token(raw);
        try{return sidOf(token.get())==sid_&&elevated(token.get());}catch(...){return false;}
    }
    void pipeServer(){
        CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        PSECURITY_DESCRIPTOR descriptor=nullptr;auto sddl=L"D:P(A;;GA;;;SY)(A;;GA;;;"+wide(sid_)+L")";
        if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr)){stopping_=true;CoUninitialize();return;}
        SECURITY_ATTRIBUTES security{sizeof(security),descriptor,FALSE};
        while(!stopping_){
             Handle pipe(CreateNamedPipeW(pipeName_.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED,PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,10000,&security));
             if(!pipe){if(logger_)logger_->error("pipe.create.failed",std::to_string(GetLastError()));Sleep(250);continue;}
            Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED connect{};connect.hEvent=event.get();bool ready=ConnectNamedPipe(pipe.get(),&connect);auto error=GetLastError();if(!ready&&error==ERROR_PIPE_CONNECTED)ready=true;
            while(!ready&&!stopping_&&error==ERROR_IO_PENDING){if(WaitForSingleObject(event.get(),250)==WAIT_OBJECT_0){DWORD n;ready=GetOverlappedResult(pipe.get(),&connect,&n,FALSE);break;}}
            if(!ready){CancelIoEx(pipe.get(),nullptr);if(error==ERROR_IO_PENDING)WaitForSingleObject(event.get(),INFINITE);continue;}
            std::string input;bool complete=false;auto deadline=std::chrono::steady_clock::now()+10s;
            while(!stopping_&&std::chrono::steady_clock::now()<deadline){char buffer[4096];OVERLAPPED read{};ResetEvent(event.get());read.hEvent=event.get();DWORD n=0;BOOL got=ReadFile(pipe.get(),buffer,sizeof(buffer),&n,&read);
                if(!got&&GetLastError()==ERROR_IO_PENDING){if(WaitForSingleObject(event.get(),5000)!=WAIT_OBJECT_0){CancelIoEx(pipe.get(),&read);WaitForSingleObject(event.get(),INFINITE);break;}got=GetOverlappedResult(pipe.get(),&read,&n,FALSE);}
                if(!got||n==0)break;input.append(buffer,n);if(input.size()>128*1024)break;if(input.find('\n')!=std::string::npos){complete=true;break;}}
            json response;
            auto requestStarted=std::chrono::steady_clock::now();
            std::string command="unknown";
            try{
                require(complete,"请求不完整或超时。");
                require(authenticate(pipe.get()),"本机用户身份或管理员权限不匹配。");
                auto request=json::parse(input);
                if(request.is_object()&&request.contains("Command")&&request["Command"].is_string())command=request["Command"].get<std::string>();
                std::lock_guard lock(mutex_);response=dispatch(request);
                if(logger_)logger_->info(response.value("Success",false)?"request.completed":"request.rejected",command,"",std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-requestStarted).count());
            }
            catch(const std::exception& e){
                if(logger_)logger_->error("request.failed",e.what(),command);
                response={{"Success",false},{"Message",e.what()}};
            }
            auto output=response.dump()+"\n";OVERLAPPED write{};ResetEvent(event.get());write.hEvent=event.get();DWORD n=0;BOOL sent=WriteFile(pipe.get(),output.data(),static_cast<DWORD>(output.size()),&n,&write);
            if(!sent&&GetLastError()==ERROR_IO_PENDING){if(WaitForSingleObject(event.get(),5000)!=WAIT_OBJECT_0){CancelIoEx(pipe.get(),&write);WaitForSingleObject(event.get(),INFINITE);}else GetOverlappedResult(pipe.get(),&write,&n,FALSE);}
            // Wait for an acknowledgement before disconnecting; DisconnectNamedPipe otherwise
            // discards unread buffered response bytes. Bound the wait for a dead GUI client.
            char ack=0;OVERLAPPED acknowledged{};ResetEvent(event.get());acknowledged.hEvent=event.get();DWORD ackBytes=0;
            if(!ReadFile(pipe.get(),&ack,1,&ackBytes,&acknowledged)&&GetLastError()==ERROR_IO_PENDING){
                if(WaitForSingleObject(event.get(),5000)!=WAIT_OBJECT_0){CancelIoEx(pipe.get(),&acknowledged);WaitForSingleObject(event.get(),INFINITE);}
                else GetOverlappedResult(pipe.get(),&acknowledged,&ackBytes,FALSE);
            }
            DisconnectNamedPipe(pipe.get());
            if(stopping_&&window_)PostMessageW(window_,WM_CLOSE,0,0);
        }
        LocalFree(descriptor);CoUninitialize();
    }
    void monitor(){CoInitializeEx(nullptr,COINIT_MULTITHREADED);while(!stopping_){try{tick();}catch(const std::exception& e){std::lock_guard lock(mutex_);monitorStatus_["Error"]=e.what();if(logger_)logger_->error("monitor.failed",e.what());}std::unique_lock lock(wakeMutex_);wake_.wait_for(lock,60s,[&]{return stopping_||requested_;});requested_=false;}CoUninitialize();}
    void requestTick(){std::lock_guard lock(wakeMutex_);requested_=true;wake_.notify_one();}
    void openGui(){
        if(gui_&&WaitForSingleObject(gui_.get(),0)==WAIT_TIMEOUT){PostMessageW(HWND_BROADCAST,RegisterWindowMessageW(L"Mio.RestoreGui"),0,0);return;}
        fs::path exe=appDirectory()/L"Mio.exe";STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION process{};auto command=L"\""+exe.wstring()+L"\" --host-ui";
        if(CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,exe.parent_path().c_str(),&startup,&process)){gui_=Handle(process.hProcess);guiPid_=process.dwProcessId;CloseHandle(process.hThread);}else MessageBoxW(window_,L"无法打开配置界面，请检查完整发布目录。",L"Windows 端口转发",MB_ICONERROR);
    }
    void trayUpdate(){if(headless_||!window_)return;NOTIFYICONDATAW data{};data.cbSize=sizeof(data);data.hWnd=window_;data.uID=1;data.uFlags=NIF_ICON|NIF_MESSAGE|NIF_TIP;data.uCallbackMessage=WM_APP+1;data.hIcon=LoadIconW(GetModuleHandleW(nullptr),MAKEINTRESOURCEW(101));
        std::wstring tip=L"Mio · 网络工具箱";{std::lock_guard lock(mutex_);int n=0;for(auto& [id,p]:cores_)if(p.running())n++;tip+=L"\n"+std::to_wstring(n)+L" 条转发运行中";}
        wcsncpy_s(data.szTip,tip.c_str(),_TRUNCATE);trayAdded_=Shell_NotifyIconW(trayAdded_?NIM_MODIFY:NIM_ADD,&data);tray_=data;}
    static LRESULT CALLBACK windowProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam){
        auto* self=reinterpret_cast<Host*>(GetWindowLongPtrW(window,GWLP_USERDATA));if(message==WM_NCCREATE){self=static_cast<Host*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(self));self->window_=window;}
        if(self){if(message==self->openWindowMessage_){self->openGui();return 0;}if(message==self->taskbarCreated_){self->trayAdded_=false;self->trayUpdate();return 0;}if(message==WM_APP+3){self->trayUpdate();return 0;}
            if(message==WM_APP+1){if(lparam==WM_LBUTTONUP||lparam==WM_LBUTTONDBLCLK)self->openGui();if(lparam==WM_RBUTTONUP){HMENU menu=CreatePopupMenu();AppendMenuW(menu,MF_STRING,1,L"打开配置界面");AppendMenuW(menu,MF_SEPARATOR,0,nullptr);AppendMenuW(menu,MF_STRING,2,L"退出程序（停止转发）");POINT p;GetCursorPos(&p);SetForegroundWindow(window);int choice=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,p.x,p.y,0,window,nullptr);DestroyMenu(menu);PostMessageW(window,WM_NULL,0,0);if(choice==1)self->openGui();if(choice==2){self->stopping_=true;self->wake_.notify_all();PostMessageW(window,WM_CLOSE,0,0);}}return 0;}
            if(message==WM_CLOSE){self->stopping_=true;self->wake_.notify_all();DestroyWindow(window);return 0;}
            if(message==WM_DESTROY){PostQuitMessage(0);return 0;}}
        return DefWindowProcW(window,message,wparam,lparam);
    }
public:
    void cleanupForUninstall(){
        json unresolved=json::array();
        for(size_t i=firewalls_["Rules"].size();i>0;--i){
            auto entry=firewalls_["Rules"][i-1];
            try{
                removeFirewall(entry);
                firewalls_["Rules"].erase(i-1);saveFirewalls();
                logger_->info("uninstall.firewall.removed",firewallName(entry));
            }catch(const std::exception& e){
                auto scope=entry.at("Scope");
                auto details=json{{"Name",firewallName(entry)},{"Protocol",scope.value("Protocol",0)==0?"TCP":"UDP"},
                    {"Address",scope.at("ListenAddress")},{"Port",scope.at("ListenPort")},{"Reason",e.what()},{"Actual",json::array()}};
                try{details["Actual"]=findFirewall(firewallName(entry));}catch(...){ }
                unresolved.push_back(details);
                logger_->warning("uninstall.firewall.retained",e.what(),entry.at("Id").get<std::string>());
            }
        }
        auto result=json{{"Unresolved",unresolved}}.dump();
        fprintf(stdout,"%s\n",result.c_str());fflush(stdout);
    }
    Host(fs::path directory,fs::path core,std::wstring pipe,bool headless):directory_(std::move(directory)),executable_(std::move(core)),sid_(currentSid()),pipeName_(std::move(pipe)),headless_(headless){
        protectDirectory(directory_,sid_);logger_=std::make_unique<NativeLogger>(directory_/L"logs");store_=std::make_unique<SqliteStore>(directory_/L"Mio.db");store_->initialize();logger_->info("host.start","原生宿主已启动");reloadStore();}
    json dispatch(const json& request){
        std::string command=request.at("Command").get<std::string>();
        if(command=="Routes"||command=="RouteOperation"){
            auto response=routeRequest(request,appDirectory());
            if(logger_&&!response.value("Success",false))logger_->error("route.request.failed",response.value("Message",std::string{}));
            return response;
        }
        if(command=="State"){reloadStore();return {{"Success",true},{"Data",state()}};}
        if(command=="Shutdown"){if(logger_)logger_->info("host.shutdown","收到退出请求");stopping_=true;wake_.notify_all();return {{"Success",true},{"Message","宿主正在退出。"}};}
        if(command=="Operation"){
            auto begin=std::chrono::steady_clock::now();operate(request);requestTick();
            std::string ruleId;
            auto expected=request.find("Expected");
            if(expected!=request.end()&&expected->is_object())ruleId=expected->value("Id",std::string{});
            if(logger_)logger_->info("operation",request.value("Operation",std::string("unknown")),ruleId,std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-begin).count());
            return {{"Success",true},{"Message","操作已完成。"}};
        }
        if(command=="Batch"){
            auto items=request.at("Items");require(items.is_array()&&!items.empty()&&items.size()<=100,"每批请选择 1–100 条规则。");std::set<std::string> keys;
            for(auto& item:items){auto op=item.at("Operation").get<std::string>();require(op=="Enable"||op=="Disable"||op=="Delete","不支持的批量操作。");require(keys.insert(parseRule(item.at("Original")).key()).second,"批量规则重复。");}
            json results=json::array();int succeeded=0;std::string errors;
            for(auto& item:items){auto listener=parseRule(item.at("Original")).listener();try{operate(item);results.push_back({{"Listener",listener},{"Success",true},{"Message","成功"}});succeeded++;}catch(const std::exception& e){results.push_back({{"Listener",listener},{"Success",false},{"Message",e.what()}});errors+="\n"+listener+"："+e.what();}}
            requestTick();return {{"Success",succeeded==static_cast<int>(items.size())},{"Message","批量操作："+std::to_string(succeeded)+" 项成功，"+std::to_string(items.size()-succeeded)+" 项失败。"+errors},{"Items",results}};
        }
        if(command=="ConfigureFirewall"){configureFirewall(request);return {{"Success",true},{"Message","防火墙规则已保存。"}};}
        if(command=="RemoveFirewall"){deleteFirewall(request);return {{"Success",true},{"Message","防火墙规则已删除。"}};}
        throw std::runtime_error("不支持的宿主请求。");
    }
    void run(bool showGui){
        if(!headless_){taskbarCreated_=RegisterWindowMessageW(L"TaskbarCreated");openWindowMessage_=RegisterWindowMessageW(L"Mio.OpenConfig");WNDCLASSW cls{};cls.lpfnWndProc=windowProc;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=L"Mio.NativeTray";require(RegisterClassW(&cls)!=0,"无法创建托盘窗口。");window_=CreateWindowExW(0,cls.lpszClassName,L"Mio.NativeHost",0,0,0,0,0,nullptr,nullptr,cls.hInstance,this);require(window_!=nullptr,"无法创建托盘窗口。");trayUpdate();}
        server_=std::thread([this]{pipeServer();});monitor_=std::thread([this]{monitor();});if(showGui&&!headless_)openGui();
        if(headless_){while(!stopping_)Sleep(100);}else{MSG msg;while(GetMessageW(&msg,nullptr,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}}
        stopping_=true;wake_.notify_all();if(server_.joinable())server_.join();if(monitor_.joinable())monitor_.join();
        {std::lock_guard lock(mutex_);for(auto& [id,p]:cores_)stopCore(p);cores_.clear();}
        if(trayAdded_)Shell_NotifyIconW(NIM_DELETE,&tray_);
        if(!headless_)PostMessageW(HWND_BROADCAST,RegisterWindowMessageW(L"Mio.HostStopped"),0,0);
    }
};

int wmain(int argc,wchar_t** argv){
    WSADATA data{};WSAStartup(MAKEWORD(2,2),&data);CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    try{
        auto sid=currentSid();HANDLE raw=nullptr;require(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&raw),"无法检查管理员权限。");Handle token(raw);require(elevated(token.get()),"请以管理员身份启动宿主。");
        auto directory=appDirectory()/L"data";auto core=appDirectory()/L"CoreBin"/L"realm.exe";auto pipe=std::wstring(LR"(\\.\pipe\Mio.Native.)")+wide(sid);bool headless=false,showGui=true;
        bool uninstall=argc==2&&std::wstring(argv[1])==L"--uninstall-cleanup";
        if(uninstall){
            Handle lock(CreateMutexW(nullptr,TRUE,(L"Global\\Mio.NativeHost."+std::to_wstring(std::hash<std::wstring>{}(directory.wstring()))).c_str()));
            require(static_cast<bool>(lock)&&GetLastError()!=ERROR_ALREADY_EXISTS,"MIO 仍在运行，请退出后重试卸载。");
            if(fs::exists(directory/L"Mio.db")){
                require(!(GetFileAttributesW((directory/L"Mio.db").c_str())&FILE_ATTRIBUTE_REPARSE_POINT),"数据库不能是链接。");
                Host host(directory,core,pipe,true);host.cleanupForUninstall();
            }
            CoUninitialize();WSACleanup();return 0;
        }
        for(int i=1;i<argc;i++){std::wstring arg=argv[i];if(arg==L"--headless"){headless=true;showGui=false;}else if(arg==L"--no-ui")showGui=false;else if(arg==L"--config-directory"&&i+1<argc)directory=argv[++i];else if(arg==L"--core"&&i+1<argc)core=argv[++i];else if(arg==L"--pipe"&&i+1<argc)pipe=std::wstring(LR"(\\.\pipe\)")+argv[++i];else throw std::runtime_error("启动参数无效。");}
        // Machine configuration has exactly one owner, including across user sessions.
        Handle single(CreateMutexW(nullptr,TRUE,(L"Global\\Mio.NativeHost."+std::to_wstring(std::hash<std::wstring>{}(directory.wstring()))).c_str()));
        require(static_cast<bool>(single),"无法取得宿主单实例锁，可能已由其他用户运行。");
        if(GetLastError()==ERROR_ALREADY_EXISTS){if(!headless&&showGui)PostMessageW(HWND_BROADCAST,RegisterWindowMessageW(L"Mio.OpenConfig"),0,0);return 0;}
        Host host(directory,core,pipe,headless);host.run(showGui);
    }catch(const std::exception& e){try{NativeLogger(appDirectory()/L"data"/L"logs").error("host.fatal",e.what());}catch(...){ }auto text=wide(e.what());if(argc>1)fprintf(stderr,"%s\n",e.what());else MessageBoxW(nullptr,text.c_str(),L"Windows 端口转发",MB_ICONERROR);CoUninitialize();WSACleanup();return 1;}
    CoUninitialize();WSACleanup();return 0;
}
