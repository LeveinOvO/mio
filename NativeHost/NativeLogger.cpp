#include "NativeLogger.h"
#include "vendor/json.hpp"
#include <windows.h>
#include <fstream>
#include <vector>
#include <algorithm>
using json=nlohmann::json;
static std::string utc(){SYSTEMTIME t{};GetSystemTime(&t);char v[40];snprintf(v,sizeof(v),"%04d-%02d-%02dT%02d:%02d:%02d.%03dZ",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds);return v;}
NativeLogger::NativeLogger(std::filesystem::path directory):directory_(std::move(directory)){std::error_code e;std::filesystem::create_directories(directory_,e);}
void NativeLogger::info(const std::string&e,const std::string&m,const std::string&r,long long d){write("Info",e,m,r,d);}
void NativeLogger::warning(const std::string&e,const std::string&m,const std::string&r,long long d){write("Warning",e,m,r,d);}
void NativeLogger::error(const std::string&e,const std::string&m,const std::string&r,long long d){write("Error",e,m,r,d);}
void NativeLogger::write(const char*l,const std::string&e,const std::string&m,const std::string&r,long long d){std::lock_guard lock(mutex_);try{auto file=directory_/("Mio-"+utc().substr(0,10)+".jsonl");if(std::filesystem::exists(file)&&std::filesystem::file_size(file)>10*1024*1024){auto rotated=file;rotated.replace_extension(".1.jsonl");std::error_code x;std::filesystem::remove(rotated,x);std::filesystem::rename(file,rotated,x);}json j={{"time",utc()},{"level",l},{"event",e}};if(!m.empty())j["message"]=m;if(!r.empty())j["ruleId"]=r;if(d>=0)j["durationMs"]=d;std::ofstream out(file,std::ios::app);if(out)out<<j.dump()<<'\n';prune();}catch(...){}}
void NativeLogger::prune(){std::vector<std::filesystem::directory_entry> files;for(auto&e:std::filesystem::directory_iterator(directory_))if(e.is_regular_file()&&e.path().extension()==".jsonl")files.push_back(e);std::sort(files.begin(),files.end(),[](auto&a,auto&b){return a.last_write_time()>b.last_write_time();});auto cutoff=std::chrono::system_clock::now()-std::chrono::hours(24*7);size_t total=0;for(auto&e:files){auto age=std::chrono::file_clock::to_sys(e.last_write_time());auto size=e.file_size();if(age<cutoff||total+size>100*1024*1024){std::error_code x;std::filesystem::remove(e,x);}else total+=size;}}
