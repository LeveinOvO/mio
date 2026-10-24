#pragma once
#include <functional>

static std::set<std::string> localIPv4Addresses() {
    ULONG size=16384;std::vector<unsigned char> memory(size);
    ULONG status=GetAdaptersAddresses(AF_INET,GAA_FLAG_SKIP_ANYCAST|GAA_FLAG_SKIP_MULTICAST|GAA_FLAG_SKIP_DNS_SERVER,nullptr,reinterpret_cast<IP_ADAPTER_ADDRESSES*>(memory.data()),&size);
    if(status==ERROR_BUFFER_OVERFLOW){memory.resize(size);status=GetAdaptersAddresses(AF_INET,GAA_FLAG_SKIP_ANYCAST|GAA_FLAG_SKIP_MULTICAST|GAA_FLAG_SKIP_DNS_SERVER,nullptr,reinterpret_cast<IP_ADAPTER_ADDRESSES*>(memory.data()),&size);}
    require(status==NO_ERROR,"无法读取本机地址，未启动转发。");
    std::set<std::string> result;
    for(auto* adapter=reinterpret_cast<IP_ADAPTER_ADDRESSES*>(memory.data());adapter;adapter=adapter->Next)
        for(auto* addr=adapter->FirstUnicastAddress;addr;addr=addr->Next){
            if(addr->Address.lpSockaddr->sa_family!=AF_INET)continue;
            auto ip=reinterpret_cast<sockaddr_in*>(addr->Address.lpSockaddr)->sin_addr;
            wchar_t out[INET_ADDRSTRLEN];InetNtopW(AF_INET,&ip,out,INET_ADDRSTRLEN);result.insert(utf8(out));
        }
    return result;
}

static void validateForwardGraph(const std::vector<Rule>& rules,const std::set<std::string>& locals) {
    auto local=[&](const std::string& ip){return ip.rfind("127.",0)==0||locals.contains(ip);};
    std::vector<int> mark(rules.size());
    std::function<void(size_t)> visit=[&](size_t i){
        require(mark[i]!=1,"检测到本机转发回环，请修改目标地址或端口。");
        if(mark[i]==2)return;mark[i]=1;
        const auto& source=rules[i];
        require(source.target!="0.0.0.0"&&source.target!="255.255.255.255","目标地址不能是未指定地址或广播地址。");
        if(local(source.target))for(size_t j=0;j<rules.size();++j){
            const auto& dest=rules[j];
            if(source.protocol==dest.protocol&&source.targetPort==dest.port&&(dest.listen=="0.0.0.0"||dest.listen==source.target))visit(j);
        }
        mark[i]=2;
    };
    for(size_t i=0;i<rules.size();++i)visit(i);
}

static void checkRealmRuntime() {
    // Search system32 only; a random DLL in the working directory is not a prerequisite.
    HMODULE module=LoadLibraryExW(L"vcruntime140.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
    require(module!=nullptr,"缺少或无法加载 Microsoft Visual C++ x64 运行库，请先安装官方 VC++ 2015–2022 Redistributable。");
    FreeLibrary(module);
}
