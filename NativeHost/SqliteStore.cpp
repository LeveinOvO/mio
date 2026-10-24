#include "SqliteStore.h"
#include <stdexcept>

static void checkSql(int result, sqlite3* db, const char* action)
{
    if (result != SQLITE_OK && result != SQLITE_DONE && result != SQLITE_ROW)
        throw std::runtime_error(std::string(action) + ": " + sqlite3_errmsg(db));
}

SqliteStore::SqliteStore(std::filesystem::path path) : path_(std::move(path))
{
    std::filesystem::create_directories(path_.parent_path());
    checkSql(sqlite3_open_v2(path_.string().c_str(), &db_, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nullptr), db_, "打开 SQLite");
    sqlite3_busy_timeout(db_, 5000);
}
SqliteStore::~SqliteStore() { if (db_) sqlite3_close_v2(db_); }
void SqliteStore::exec(const char* sql)
{
    char* error = nullptr; auto result = sqlite3_exec(db_, sql, nullptr, nullptr, &error);
    if (result != SQLITE_OK) { std::string message = error ? error : "SQLite error"; sqlite3_free(error); throw std::runtime_error(message); }
}
void SqliteStore::initialize()
{
    std::lock_guard lock(mutex_);
    exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
    exec("CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
         "CREATE TABLE IF NOT EXISTS rules (id TEXT PRIMARY KEY, desired_json TEXT NOT NULL, applied_json TEXT NOT NULL, enabled INTEGER NOT NULL, message TEXT NOT NULL, last_checked TEXT);"
         "CREATE TABLE IF NOT EXISTS firewall_rules (id TEXT PRIMARY KEY, scope_json TEXT NOT NULL, expected_json TEXT, message TEXT NOT NULL);"
         "CREATE TABLE IF NOT EXISTS runtime (rule_id TEXT PRIMARY KEY, pid INTEGER, running INTEGER NOT NULL, error TEXT NOT NULL, updated_at TEXT NOT NULL);"
         "PRAGMA user_version=1;");
}
nlohmann::json SqliteStore::readRules()
{
    std::lock_guard lock(mutex_); nlohmann::json rows = nlohmann::json::array();
    sqlite3_stmt* statement = nullptr; checkSql(sqlite3_prepare_v2(db_, "SELECT id,desired_json,applied_json,enabled,message,last_checked FROM rules ORDER BY rowid", -1, &statement, nullptr), db_, "读取规则");
    while (sqlite3_step(statement) == SQLITE_ROW) rows.push_back({{"Id",reinterpret_cast<const char*>(sqlite3_column_text(statement,0))},{"Desired",nlohmann::json::parse(reinterpret_cast<const char*>(sqlite3_column_text(statement,1)))},{"Applied",nlohmann::json::parse(reinterpret_cast<const char*>(sqlite3_column_text(statement,2)))},{"Enabled",sqlite3_column_int(statement,3)!=0},{"Message",reinterpret_cast<const char*>(sqlite3_column_text(statement,4))},{"LastChecked",sqlite3_column_text(statement,5)?reinterpret_cast<const char*>(sqlite3_column_text(statement,5)):nullptr}});
    sqlite3_finalize(statement); return rows;
}
nlohmann::json SqliteStore::readFirewalls()
{
    std::lock_guard lock(mutex_); nlohmann::json rows = nlohmann::json::array();
    sqlite3_stmt* statement=nullptr;checkSql(sqlite3_prepare_v2(db_,"SELECT id,scope_json,expected_json,message FROM firewall_rules ORDER BY rowid",-1,&statement,nullptr),db_,"读取防火墙规则");
    while(sqlite3_step(statement)==SQLITE_ROW) rows.push_back({{"Id",reinterpret_cast<const char*>(sqlite3_column_text(statement,0))},{"Scope",nlohmann::json::parse(reinterpret_cast<const char*>(sqlite3_column_text(statement,1)))},{"Expected",sqlite3_column_text(statement,2)?nlohmann::json::parse(reinterpret_cast<const char*>(sqlite3_column_text(statement,2))):nullptr},{"Message",reinterpret_cast<const char*>(sqlite3_column_text(statement,3))}});
    sqlite3_finalize(statement);return rows;
}
nlohmann::json SqliteStore::readSettings(){return nlohmann::json{{"KeepInTray",getSetting("KeepInTray","true")=="true"}};}
void SqliteStore::replaceRules(const nlohmann::json& rules)
{
    std::lock_guard lock(mutex_);exec("DELETE FROM rules;");sqlite3_stmt* statement=nullptr;checkSql(sqlite3_prepare_v2(db_,"INSERT INTO rules(id,desired_json,applied_json,enabled,message,last_checked) VALUES(?,?,?,?,?,?)",-1,&statement,nullptr),db_,"写入规则");
    for(auto& row:rules){auto id=row.at("Id").get<std::string>();auto desired=row.at("Desired").dump(),applied=row.at("Applied").dump();auto message=row.value("Message",std::string{});sqlite3_bind_text(statement,1,id.c_str(),-1,SQLITE_TRANSIENT);sqlite3_bind_text(statement,2,desired.c_str(),-1,SQLITE_TRANSIENT);sqlite3_bind_text(statement,3,applied.c_str(),-1,SQLITE_TRANSIENT);sqlite3_bind_int(statement,4,row.at("Enabled").get<bool>());sqlite3_bind_text(statement,5,message.c_str(),-1,SQLITE_TRANSIENT);auto checked=row.value("LastChecked",nlohmann::json(nullptr));if(checked.is_null())sqlite3_bind_null(statement,6);else{auto checkedText=checked.get<std::string>();sqlite3_bind_text(statement,6,checkedText.c_str(),-1,SQLITE_TRANSIENT);}checkSql(sqlite3_step(statement),db_,"写入规则");sqlite3_reset(statement);sqlite3_clear_bindings(statement);}
    sqlite3_finalize(statement);
}
void SqliteStore::replaceFirewalls(const nlohmann::json& rules)
{
    std::lock_guard lock(mutex_);exec("DELETE FROM firewall_rules;");sqlite3_stmt* statement=nullptr;checkSql(sqlite3_prepare_v2(db_,"INSERT INTO firewall_rules(id,scope_json,expected_json,message) VALUES(?,?,?,?)",-1,&statement,nullptr),db_,"写入防火墙配置");
    for(auto& row:rules){auto id=row.at("Id").get<std::string>(),scope=row.at("Scope").dump(),message=row.value("Message",std::string{});sqlite3_bind_text(statement,1,id.c_str(),-1,SQLITE_TRANSIENT);sqlite3_bind_text(statement,2,scope.c_str(),-1,SQLITE_TRANSIENT);if(row.at("Expected").is_null())sqlite3_bind_null(statement,3);else{auto expected=row.at("Expected").dump();sqlite3_bind_text(statement,3,expected.c_str(),-1,SQLITE_TRANSIENT);}sqlite3_bind_text(statement,4,message.c_str(),-1,SQLITE_TRANSIENT);checkSql(sqlite3_step(statement),db_,"写入防火墙配置");sqlite3_reset(statement);sqlite3_clear_bindings(statement);}
    sqlite3_finalize(statement);
}
void SqliteStore::setSetting(const std::string& key,const std::string& value){std::lock_guard lock(mutex_);sqlite3_stmt* s=nullptr;checkSql(sqlite3_prepare_v2(db_,"INSERT INTO metadata(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",-1,&s,nullptr),db_,"写入设置");sqlite3_bind_text(s,1,key.c_str(),-1,SQLITE_TRANSIENT);sqlite3_bind_text(s,2,value.c_str(),-1,SQLITE_TRANSIENT);checkSql(sqlite3_step(s),db_,"写入设置");sqlite3_finalize(s);}
std::string SqliteStore::getSetting(const std::string& key,const std::string& fallback){std::lock_guard lock(mutex_);sqlite3_stmt* s=nullptr;checkSql(sqlite3_prepare_v2(db_,"SELECT value FROM metadata WHERE key=?",-1,&s,nullptr),db_,"读取设置");sqlite3_bind_text(s,1,key.c_str(),-1,SQLITE_TRANSIENT);auto result=sqlite3_step(s)==SQLITE_ROW?std::string(reinterpret_cast<const char*>(sqlite3_column_text(s,0))):fallback;sqlite3_finalize(s);return result;}
void SqliteStore::transaction(const std::function<void()>& callback){std::lock_guard lock(mutex_);exec("BEGIN IMMEDIATE;");try{callback();exec("COMMIT;");}catch(...){try{exec("ROLLBACK;");}catch(...){}throw;}}
