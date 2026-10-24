#pragma once
#include "vendor/sqlite/sqlite3.h"
#include <filesystem>
#include <string>
#include <vector>
#include <optional>
#include <mutex>
#include <functional>
#include "vendor/json.hpp"

class SqliteStore
{
    sqlite3* db_ = nullptr;
    std::filesystem::path path_;
    std::mutex mutex_;
public:
    explicit SqliteStore(std::filesystem::path path);
    ~SqliteStore();
    SqliteStore(const SqliteStore&) = delete;
    void initialize();
    nlohmann::json readRules();
    nlohmann::json readFirewalls();
    nlohmann::json readSettings();
    void replaceRules(const nlohmann::json& rules);
    void replaceFirewalls(const nlohmann::json& rules);
    void setSetting(const std::string& key, const std::string& value);
    std::string getSetting(const std::string& key, const std::string& fallback);
    void transaction(const std::function<void()>& callback);
private:
    void exec(const char* sql);
};
