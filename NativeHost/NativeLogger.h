#pragma once
#include <filesystem>
#include <mutex>
#include <string>

class NativeLogger
{
    std::filesystem::path directory_;
    std::mutex mutex_;
public:
    explicit NativeLogger(std::filesystem::path directory);
    void info(const std::string& event, const std::string& message = "", const std::string& ruleId = "", long long durationMs = -1);
    void warning(const std::string& event, const std::string& message = "", const std::string& ruleId = "", long long durationMs = -1);
    void error(const std::string& event, const std::string& message = "", const std::string& ruleId = "", long long durationMs = -1);
private:
    void write(const char* level, const std::string& event, const std::string& message, const std::string& ruleId, long long durationMs);
    void prune();
};
