#include "libretro.h"
#include <windows.h>
#include <iostream>
#include <fstream>
#include <string>
#include <vector>
#include <sstream>
#include <algorithm>
#include <cstdint>
#include <cstring>

// Simple MD5 Implementation
namespace MD5 {
    uint32_t rotl(uint32_t x, uint32_t n) { return (x << n) | (x >> (32 - n)); }
    void process(uint32_t* state, const uint8_t* block) {
        uint32_t a = state[0], b = state[1], c = state[2], d = state[3];
        uint32_t x[16];
        for (int i = 0; i < 16; ++i) x[i] = block[i * 4] | (block[i * 4 + 1] << 8) | (block[i * 4 + 2] << 16) | (block[i * 4 + 3] << 24);
        
        auto ff = [&](uint32_t& a, uint32_t b, uint32_t c, uint32_t d, uint32_t x, uint32_t s, uint32_t ac) { a = rotl(a + ((b & c) | (~b & d)) + x + ac, s) + b; };
        auto gg = [&](uint32_t& a, uint32_t b, uint32_t c, uint32_t d, uint32_t x, uint32_t s, uint32_t ac) { a = rotl(a + ((b & d) | (c & ~d)) + x + ac, s) + b; };
        auto hh = [&](uint32_t& a, uint32_t b, uint32_t c, uint32_t d, uint32_t x, uint32_t s, uint32_t ac) { a = rotl(a + (b ^ c ^ d) + x + ac, s) + b; };
        auto ii = [&](uint32_t& a, uint32_t b, uint32_t c, uint32_t d, uint32_t x, uint32_t s, uint32_t ac) { a = rotl(a + (c ^ (b | ~d)) + x + ac, s) + b; };

        ff(a,b,c,d,x[0],7,0xd76aa478); ff(d,a,b,c,x[1],12,0xe8c7b756); ff(c,d,a,b,x[2],17,0x242070db); ff(b,c,d,a,x[3],22,0xc1bdceee);
        ff(a,b,c,d,x[4],7,0xf57c0faf); ff(d,a,b,c,x[5],12,0x4787c62a); ff(c,d,a,b,x[6],17,0xa8304613); ff(b,c,d,a,x[7],22,0xfd469501);
        ff(a,b,c,d,x[8],7,0x698098d8); ff(d,a,b,c,x[9],12,0x8b44f7af); ff(c,d,a,b,x[10],17,0xffff5bb1); ff(b,c,d,a,x[11],22,0x895cd7be);
        ff(a,b,c,d,x[12],7,0x6b901122); ff(d,a,b,c,x[13],12,0xfd987193); ff(c,d,a,b,x[14],17,0xa679438e); ff(b,c,d,a,x[15],22,0x49b40821);

        gg(a,b,c,d,x[1],5,0xf61e2562); gg(d,a,b,c,x[6],9,0xc040b340); gg(c,d,a,b,x[11],14,0x265e5a51); gg(b,c,d,a,x[0],20,0xe9b6c7aa);
        gg(a,b,c,d,x[5],5,0xd62f105d); gg(d,a,b,c,x[10],9,0x02441453); gg(c,d,a,b,x[15],14,0xd8a1e681); gg(b,c,d,a,x[4],20,0xe7d3fbc8);
        gg(a,b,c,d,x[9],5,0x21e1cde6); gg(d,a,b,c,x[14],9,0xc33707d6); gg(c,d,a,b,x[3],14,0xf4d50d87); gg(b,c,d,a,x[8],20,0x455a14ed);
        gg(a,b,c,d,x[13],5,0xa9e3e905); gg(d,a,b,c,x[2],9,0xfcefa3f8); gg(c,d,a,b,x[7],14,0x676f02d9); gg(b,c,d,a,x[12],20,0x8d2a4c8a);

        hh(a,b,c,d,x[5],4,0xfffa3942); hh(d,a,b,c,x[8],11,0x8771f681); hh(c,d,a,b,x[11],16,0x6d9d6122); hh(b,c,d,a,x[14],23,0xfde5380c);
        hh(a,b,c,d,x[1],4,0xa4beea44); hh(d,a,b,c,x[4],11,0x4bdecfa9); hh(c,d,a,b,x[7],16,0xf6bb4b60); hh(b,c,d,a,x[10],23,0xbebfbc70);
        hh(a,b,c,d,x[13],4,0x289b7ec6); hh(d,a,b,c,x[0],11,0xeaa127fa); hh(c,d,a,b,x[3],16,0xd4ef3085); hh(b,c,d,a,x[6],23,0x04881d05);
        hh(a,b,c,d,x[9],4,0xd9d4d039); hh(d,a,b,c,x[12],11,0xe6db99e5); hh(c,d,a,b,x[15],16,0x1fa27cf8); hh(b,c,d,a,x[2],23,0xc4ac5665);

        ii(a,b,c,d,x[0],6,0xf4292244); ii(d,a,b,c,x[7],10,0x432aff97); ii(c,d,a,b,x[14],15,0xab9423a7); ii(b,c,d,a,x[5],21,0xfc93a039);
        ii(a,b,c,d,x[12],6,0x655b59c3); ii(d,a,b,c,x[3],10,0x8f0ccc92); ii(c,d,a,b,x[10],15,0xffeff47d); ii(b,c,d,a,x[1],21,0x85845dd1);
        ii(a,b,c,d,x[8],6,0x6fa87e4f); ii(d,a,b,c,x[15],10,0xfe2ce6e0); ii(c,d,a,b,x[6],15,0xa3014314); ii(b,c,d,a,x[13],21,0x4e0811a1);
        ii(a,b,c,d,x[4],6,0xf7537e82); ii(d,a,b,c,x[11],10,0xbd3af235); ii(c,d,a,b,x[2],15,0x2ad7d2bb); ii(b,c,d,a,x[9],21,0xeb86d391);

        state[0] += a; state[1] += b; state[2] += c; state[3] += d;
    }

    std::string compute(const uint8_t* data, size_t len) {
        uint32_t state[4] = { 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476 };
        size_t offset = 0;
        while (offset + 64 <= len) { process(state, data + offset); offset += 64; }
        
        uint8_t pad[128]; 
        size_t pad_len = len - offset;
        memcpy(pad, data + offset, pad_len);
        pad[pad_len++] = 0x80;
        if (pad_len > 56) { while (pad_len < 64) pad[pad_len++] = 0; process(state, pad); pad_len = 0; }
        while (pad_len < 56) pad[pad_len++] = 0;
        uint64_t bits = (uint64_t)len * 8;
        for (int i = 0; i < 8; ++i) pad[56 + i] = (uint8_t)(bits >> (i * 8));
        process(state, pad);

        char out[33];
        for (int i = 0; i < 4; ++i) snprintf(out + i * 8, 9, "%02x%02x%02x%02x", 
            (uint8_t)(state[i]), (uint8_t)(state[i] >> 8), (uint8_t)(state[i] >> 16), (uint8_t)(state[i] >> 24));
        return std::string(out);
    }
}

// Pointers vers les fonctions du VRAI core
void (*real_retro_init)(void);
void (*real_retro_deinit)(void);
unsigned (*real_retro_api_version)(void);
void (*real_retro_get_system_info)(struct retro_system_info *info);
void (*real_retro_get_system_av_info)(struct retro_system_av_info *info);
void (*real_retro_set_environment)(retro_environment_t);
void (*real_retro_set_video_refresh)(retro_video_refresh_t);
void (*real_retro_set_audio_sample)(retro_audio_sample_t);
void (*real_retro_set_audio_sample_batch)(retro_audio_sample_batch_t);
void (*real_retro_set_input_poll)(retro_input_poll_t);
void (*real_retro_set_input_state)(retro_input_state_t);
void (*real_retro_set_controller_port_device)(unsigned port, unsigned device);
void (*real_retro_reset)(void);
void (*real_retro_run)(void);
size_t (*real_retro_serialize_size)(void);
bool (*real_retro_serialize)(void *data, size_t size);
bool (*real_retro_unserialize)(const void *data, size_t size);
void (*real_retro_cheat_reset)(void);
void (*real_retro_cheat_set)(unsigned index, bool enabled, const char *code);
bool (*real_retro_load_game)(const struct retro_game_info *game);
bool (*real_retro_load_game_special)(unsigned game_type, const struct retro_game_info *info, size_t num_info);
void (*real_retro_unload_game)(void);
unsigned (*real_retro_get_region)(void);
void *(*real_retro_get_memory_data)(unsigned id);
size_t (*real_retro_get_memory_size)(unsigned id);

HMODULE hRealCore = NULL;
HANDLE hPipe = INVALID_HANDLE_VALUE;
HINSTANCE hInstDLL = NULL;

extern "C" __declspec(dllexport) const char* GetWrapperSignature() {
    return "RETROBAT_ARCADE_WRAPPER_V1_DO_NOT_DELETE";
}

std::string g_last_mem_path = "";
std::string g_load_error = "";
std::string g_alias_applied = "";
bool g_mapping_error_logged = false;

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        hInstDLL = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

std::string current_game_name = "unknown_game";
std::string current_core_name = "unknown_core";

struct MappedResult {
    std::string action;
    bool no_log;
    bool no_survey;
    bool found;
};

struct MemWatch {
    unsigned int address;
    unsigned int size;
    bool is_big_endian;
    bool no_log;
    bool no_survey;
    unsigned int mask;
    std::string name;
    
    unsigned int min_val;
    unsigned int max_val;
    unsigned int factor;
    bool has_min;
    bool has_max;
    bool is_score;
    
    std::string category;
    std::string event;
    std::string condition;
    unsigned int condition_value;
    std::string action;
    std::string raw_map;
    std::string raw_action_map;

    unsigned int frames_since_change;
    unsigned int last_gap;
    unsigned int regular_gap_count;
    bool is_permanently_muted;

    unsigned int last_value;
    bool initialized;
    bool needs_byte_swap;
};
std::vector<MemWatch> mem_watches;

size_t findClosingBrace(const std::string& str, size_t start) {
    int depth = 0;
    for (size_t i = start; i < str.length(); ++i) {
        if (str[i] == '{') depth++;
        else if (str[i] == '}') {
            depth--;
            if (depth == 0) return i;
        }
    }
    return std::string::npos;
}

MappedResult getMappedValueExt(const std::string& raw_map, unsigned int val) {
    MappedResult res = {"", false, false, false};
    if (raw_map.empty()) return res;
    
    char buf1[32], buf2[32], buf3[32], buf4[32], buf5[32];
    snprintf(buf1, sizeof(buf1), "[%u]", val);
    snprintf(buf2, sizeof(buf2), "[0x%X]", val);
    snprintf(buf3, sizeof(buf3), "[0x%x]", val);
    snprintf(buf4, sizeof(buf4), "[0x%02X]", val);
    snprintf(buf5, sizeof(buf5), "[0x%02x]", val);
    
    const char* targets[] = {buf1, buf2, buf3, buf4, buf5};
    for (int i = 0; i < 5; ++i) {
        size_t f = raw_map.find(targets[i]);
        if (f != std::string::npos) {
            size_t eq = raw_map.find("=", f + strlen(targets[i]));
            if (eq == std::string::npos) continue;
            
            size_t val_start = eq + 1;
            while (val_start < raw_map.length() && raw_map[val_start] == ' ') val_start++;
            
            if (val_start < raw_map.length() && raw_map[val_start] == '{') {
                size_t end = findClosingBrace(raw_map, val_start);
                if (end != std::string::npos) {
                    std::string entry = raw_map.substr(val_start, end - val_start + 1);
                    res.found = true;
                    res.no_log = (entry.find("no_log=true") != std::string::npos || entry.find("no_log = true") != std::string::npos);
                    res.no_survey = (entry.find("no_survey=true") != std::string::npos || entry.find("no_survey = true") != std::string::npos);
                    
                    size_t act_p = entry.find("action=\"");
                    if (act_p == std::string::npos) act_p = entry.find("action = \"");
                    if (act_p != std::string::npos) {
                        size_t s = entry.find("\"", act_p);
                        size_t e = entry.find("\"", s + 1);
                        if (s != std::string::npos && e != std::string::npos) res.action = entry.substr(s+1, e-s-1);
                    }
                    return res;
                }
            } else if (val_start < raw_map.length() && raw_map[val_start] == '\"') {
                size_t e = raw_map.find("\"", val_start + 1);
                if (e != std::string::npos) {
                    res.action = raw_map.substr(val_start + 1, e - val_start - 1);
                    res.found = true;
                    return res;
                }
            }
        }
    }
    return res;
}

unsigned int bcd_to_int(unsigned int val) {
    unsigned int dec = 0;
    unsigned int mult = 1;
    while (val > 0) {
        unsigned int digit = val & 0xF;
        if (digit > 9) return val; // Fallback mathématique si la RAM n'est pas du vrai BCD
        dec += digit * mult;
        mult *= 10;
        val >>= 4;
    }
    return dec;
}

void extractLuaKeys(const std::string& content, size_t pos, std::string& category, std::string& event) {
    category = "unknown";
    event = "unknown";
    
    auto countSpaces = [](const std::string& s) {
        size_t n = 0;
        while (n < s.length() && s[n] == ' ') n++;
        return n;
    };

    // Trouver le début de la ligne actuelle pour mesurer l'indentation
    size_t line_start = content.rfind("\n", pos);
    if (line_start == std::string::npos) line_start = 0; else line_start++;
    size_t current_indent = countSpaces(content.substr(line_start, pos - line_start + 10));

    size_t p = line_start;
    size_t last_indent = current_indent;
    
    // Remonter ligne par ligne
    while (p > 0) {
        size_t prev_line_end = content.rfind("\n", p > 1 ? p - 2 : 0);
        size_t s = (prev_line_end == std::string::npos) ? 0 : prev_line_end + 1;
        size_t e = content.find("\n", s);
        if (e == std::string::npos) e = content.length();
        
        std::string line = content.substr(s, e - s);
        size_t indent = countSpaces(line);
        
        size_t brace = line.find("{");
        size_t eq = line.find("=");
        
        if (brace != std::string::npos && eq != std::string::npos && eq < brace && indent < last_indent) {
            // C'est un parent !
            int name_end = (int)eq - 1;
            while (name_end >= 0 && line[name_end] == ' ') name_end--;
            int name_start = name_end;
            while (name_start >= 0 && (isalnum(line[name_start]) || line[name_start] == '_')) name_start--;
            std::string word = line.substr(name_start + 1, name_end - name_start);
            
            if (word != "map" && word != "action_map" && word != "hashes" && word != "rom" && word != "game" && !word.empty()) {
                if (event == "unknown") {
                    event = word;
                    last_indent = indent;
                } else if (word != event && word != "events") {
                    category = word;
                    return;
                }
            }
        }
        if (s == 0) break;
        p = s;
    }
}

std::string GetLogHeader(unsigned int addr = 0, unsigned int val = 0, bool has_meta = false) {
    SYSTEMTIME st;
    GetLocalTime(&st);
    char buf[128];
    if (has_meta) {
        snprintf(buf, sizeof(buf), "[%02d:%02d:%02d.%03d] [ADDR:0x%06X] [VAL:0x%02X] ", 
                 st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, addr, val);
    } else {
        snprintf(buf, sizeof(buf), "[%02d:%02d:%02d.%03d] ", 
                 st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    }
    return std::string(buf);
}

const char* PIPE_NAME = "\\\\.\\pipe\\RetroBatArcadePipe";

void ConnectPipe() {
    if (hPipe != INVALID_HANDLE_VALUE) return;
    hPipe = CreateFileA(PIPE_NAME, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
}

void LoadMemFile(std::string sys_name, std::string rom_basename, std::string rom_hash = "") {
    mem_watches.clear();
    std::string mem_filename = rom_basename + ".MEM";
    char path[MAX_PATH];
    GetModuleFileNameA(hInstDLL, path, MAX_PATH);
    std::string dllPath(path);
    std::string mem_path = "";
    std::string alias = "";
    
    auto searchAlias = [&](const std::string& alias_path) -> std::string {
        {
            std::stringstream ss; ss << GetLogHeader() << "[DOF API] Recherche dictionnaire : " << alias_path << "\n";
            std::string s_ss = ss.str(); DWORD bw; WriteFile(hPipe, s_ss.c_str(), (DWORD)s_ss.length(), &bw, NULL);
        }
        std::ifstream alias_file(alias_path);
        if (!alias_file.is_open()) return "";
        std::string line;
        while (std::getline(alias_file, line)) {
            // Priority 1: Hash match
            if (!rom_hash.empty()) {
                size_t p = line.find("\"" + rom_hash + "\"");
                if (p != std::string::npos) {
                    size_t colon = line.find(":", p);
                    if (colon != std::string::npos) {
                        size_t s = line.find("\"", colon);
                        size_t e = line.find("\"", s + 1);
                        if (s != std::string::npos && e != std::string::npos) {
                            std::string res = line.substr(s + 1, e - s - 1);
                            std::stringstream ss; ss << GetLogHeader() << "[DOF API] MATCH ALIAS (HASH) : " << rom_hash << " -> " << res << "\n";
                            std::string s_ss = ss.str(); DWORD bw; WriteFile(hPipe, s_ss.c_str(), (DWORD)s_ss.length(), &bw, NULL);
                            return res;
                        }
                    }
                }
            }
            // Priority 2: Filename match
            size_t p = line.find("\"" + rom_basename + "\"");
            if (p != std::string::npos) {
                size_t colon = line.find(":", p);
                if (colon != std::string::npos) {
                    size_t s = line.find("\"", colon);
                    size_t e = line.find("\"", s + 1);
                    if (s != std::string::npos && e != std::string::npos) {
                        std::string res = line.substr(s + 1, e - s - 1);
                        std::stringstream ss; ss << GetLogHeader() << "[DOF API] MATCH ALIAS (NAME) : " << rom_basename << " -> " << res << "\n";
                        std::string s_ss = ss.str(); DWORD bw; WriteFile(hPipe, s_ss.c_str(), (DWORD)s_ss.length(), &bw, NULL);
                        return res;
                    }
                }
            }
        }
        return "";
    };

    size_t emulators_pos = dllPath.rfind("\\emulators\\retroarch\\cores");
    std::string rootFolder = "";
    if (emulators_pos != std::string::npos) {
        rootFolder = dllPath.substr(0, emulators_pos);
        std::string apiexpose_alias = rootFolder + "\\plugins\\APIExpose\\resources\\ram\\" + sys_name + "\\alias.json";
        alias = searchAlias(apiexpose_alias);
    } 
    
    if (alias.empty()) {
        size_t last_slash = dllPath.rfind("\\");
        std::string local_mame_alias = dllPath.substr(0, last_slash + 1) + "mame\\alias.json";
        alias = searchAlias(local_mame_alias);
    }

    if (!alias.empty()) {
        g_alias_applied = alias;
        if (!rootFolder.empty()) {
             mem_path = rootFolder + "\\plugins\\APIExpose\\resources\\ram\\" + sys_name + "\\" + alias + ".MEM";
        } else {
             size_t last_slash = dllPath.rfind("\\");
             mem_path = dllPath.substr(0, last_slash + 1) + "mame\\" + alias + ".MEM";
        }
    } else {
        if (!rootFolder.empty()) {
             mem_path = rootFolder + "\\plugins\\APIExpose\\resources\\ram\\" + sys_name + "\\" + mem_filename;
        } else {
             size_t last_slash = dllPath.rfind("\\");
             mem_path = dllPath.substr(0, last_slash + 1) + "mame\\" + mem_filename;
        }
    }

    std::ifstream file(mem_path);
    if (!file.is_open()) {
        g_load_error = "Fichier MEM introuvable : " + mem_path;
        return;
    }
    
    g_last_mem_path = mem_path;
    std::stringstream load_inf;
    load_inf << GetLogHeader() << "[DOF API] Fichier MEM charge : " << mem_path << "\n";
    std::string load_s = load_inf.str();
    DWORD bw_inf; WriteFile(hPipe, load_s.c_str(), (DWORD)load_s.length(), &bw_inf, NULL);

    std::string content((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    bool file_is_megadrive = (content.find("system") != std::string::npos && 
                             (content.find("megadrive") != std::string::npos || content.find("genesis") != std::string::npos));
    
    auto toLower = [](std::string s) {
        std::string res = s;
        for (char &c : res) if (c >= 'A' && c <= 'Z') c += 32;
        return res;
    };

    size_t search_pos = 0;
    while ((search_pos = content.find("address", search_pos)) != std::string::npos) {
        size_t next_addr = content.find("address", search_pos + 7);
        std::string line_raw = content.substr(search_pos, (next_addr == std::string::npos) ? content.length() - search_pos : next_addr - search_pos);
        std::string line_l = toLower(line_raw);

        size_t eq_pos = line_l.find("=");
        if (eq_pos == std::string::npos) { search_pos = next_addr; continue; }
        size_t comma_pos = line_raw.find(",", eq_pos);
        if (comma_pos == std::string::npos) { search_pos = next_addr; continue; }
        std::string addr_str = line_raw.substr(eq_pos + 1, comma_pos - eq_pos - 1);
        while (!addr_str.empty() && (addr_str[0] == ' ' || addr_str[0] == '\t')) addr_str.erase(0, 1);

        MemWatch mw;
        try { 
            if (addr_str.find("0x") == 0 || addr_str.find("0X") == 0) mw.address = std::stoul(addr_str, nullptr, 16);
            else mw.address = std::stoul(addr_str, nullptr, 0);
        } catch(...) { search_pos = next_addr; continue; }

        extractLuaKeys(content, search_pos, mw.category, mw.event);
        
        std::string t_val = "u8";
        size_t t_p = line_l.find("type");
        if (t_p != std::string::npos) {
            size_t t_eq = line_l.find("=", t_p);
            size_t s = line_raw.find("\"", t_eq);
            size_t e = line_raw.find("\"", s + 1);
            if (s != std::string::npos && e != std::string::npos) t_val = toLower(line_raw.substr(s + 1, e - s - 1));
        }
        mw.size = 1; mw.is_big_endian = false;
        if (t_val == "u16be") { mw.size = 2; mw.is_big_endian = true; }
        else if (t_val == "u16le") { mw.size = 2; mw.is_big_endian = false; }
        else if (t_val == "u24be") { mw.size = 3; mw.is_big_endian = true; }
        else if (t_val == "u24le") { mw.size = 3; mw.is_big_endian = false; }
        else if (t_val == "u32be") { mw.size = 4; mw.is_big_endian = true; }
        else if (t_val == "u32le") { mw.size = 4; mw.is_big_endian = false; }

        mw.condition = "change";
        size_t cond_p = line_l.find("condition");
        if (cond_p != std::string::npos) {
            size_t c_eq = line_l.find("=", cond_p);
            size_t s = line_raw.find("\"", c_eq);
            size_t e = line_raw.find("\"", s + 1);
            if (s != std::string::npos && e != std::string::npos) mw.condition = toLower(line_raw.substr(s + 1, e - s - 1));
        }

        mw.condition_value = 0;
        size_t v_p = line_l.find("value");
        if (v_p != std::string::npos) {
            size_t v_eq = line_l.find("=", v_p);
            size_t v_c = line_raw.find_first_of(",}", v_eq);
            if (v_eq != std::string::npos && v_c != std::string::npos) {
                std::string v_s = line_raw.substr(v_eq + 1, v_c - v_eq - 1);
                while (!v_s.empty() && (v_s[0] == ' ' || v_s[0] == '\t')) v_s.erase(0, 1);
                try {
                    if (v_s.find("0x") == 0 || v_s.find("0X") == 0) mw.condition_value = std::stoul(v_s, nullptr, 16);
                    else mw.condition_value = std::stoul(v_s, nullptr, 0);
                } catch(...) {}
            }
        }

        mw.mask = 0xFFFFFFFF;
        size_t m_p = line_l.find("mask");
        if (m_p != std::string::npos) {
            size_t m_eq = line_l.find("=", m_p);
            size_t m_c = line_raw.find_first_of(",}", m_eq);
            if (m_eq != std::string::npos && m_c != std::string::npos) {
                std::string m_s = line_raw.substr(m_eq + 1, m_c - m_eq - 1);
                try { mw.mask = std::stoul(m_s, nullptr, 0); } catch(...) {}
            }
        }

        mw.no_log = (line_l.find("no_log=true") != std::string::npos || line_l.find("no_log = true") != std::string::npos);
        mw.no_survey = (line_l.find("no_survey=true") != std::string::npos || line_l.find("no_survey = true") != std::string::npos);
        mw.is_score = (line_l.find("is_score=true") != std::string::npos || mw.category == "scoring");
        
        mw.name = "Inconnu";
        size_t d_p = line_l.find("desc");
        if (d_p != std::string::npos) {
            size_t d_eq = line_l.find("=", d_p);
            size_t s = line_raw.find("\"", d_eq);
            size_t e = line_raw.find("\"", s + 1);
            if (s != std::string::npos && e != std::string::npos) mw.name = line_raw.substr(s + 1, e - s - 1);
        }

        mw.action = "UPDATE";
        size_t act_p = line_l.find("action");
        if (act_p != std::string::npos) {
            size_t a_eq = line_l.find("=", act_p);
            size_t s = line_raw.find("\"", a_eq);
            size_t e = line_raw.find("\"", s + 1);
            if (s != std::string::npos && e != std::string::npos) mw.action = line_raw.substr(s + 1, e - s - 1);
        }

        if (mw.no_survey) { search_pos = next_addr; continue; }

        mw.last_value = 0; mw.initialized = false;
        mw.frames_since_change = 0; mw.last_gap = 0; mw.regular_gap_count = 0; mw.is_permanently_muted = false;
        mw.needs_byte_swap = file_is_megadrive;
        mw.raw_map = ""; mw.raw_action_map = "";
        
        size_t map_p = line_l.find("map={");
        if (map_p != std::string::npos) { size_t b_s = line_raw.find("{", map_p); size_t b_e = findClosingBrace(line_raw, b_s); if (b_e != std::string::npos) mw.raw_map = line_raw.substr(b_s+1, b_e-b_s-1); }
        size_t am_p = line_l.find("action_map={");
        if (am_p != std::string::npos) { size_t b_s = line_raw.find("{", am_p); size_t b_e = findClosingBrace(line_raw, b_s); if (b_e != std::string::npos) mw.raw_action_map = line_raw.substr(b_s+1, b_e-b_s-1); }

        mem_watches.push_back(mw);
        
        std::string c_trace = mw.condition;
        if (mw.condition == "eq" || mw.condition == "equal") c_trace = "==" + std::to_string(mw.condition_value);
        else if (mw.condition == "neq" || mw.condition == "not_equal") c_trace = "!=" + std::to_string(mw.condition_value);
        else if (mw.condition == "bit_true") { char mbuf[32]; snprintf(mbuf, 32, "BIT_TRUE:0X%X", mw.mask); c_trace = mbuf; }

        std::stringstream ls;
        ls << GetLogHeader(mw.address, 0, true) << "[DOF API] LISTENING: [" << mw.category << "." << mw.event << "] -> " 
           << std::hex << std::uppercase << mw.address << std::nouppercase << std::dec << " (" << c_trace << ") [LOG:" << (mw.no_log ? "NO" : "YES") << ", SURVEY:" << (mw.no_survey ? "NO" : "YES") << "] - " << mw.name << "\n";
        std::string ls_s = ls.str();
        DWORD bwL; WriteFile(hPipe, ls_s.c_str(), (DWORD)ls_s.length(), &bwL, NULL);

        search_pos = next_addr;
    }
}

void InitProxy() {
    if (hRealCore != NULL) return;

    char path[MAX_PATH];
    GetModuleFileNameA(hInstDLL, path, MAX_PATH);
    std::string dllPath(path);
    std::string realDllPath = dllPath;
    
    size_t cores_pos = realDllPath.rfind("\\cores\\");
    if (cores_pos != std::string::npos) {
        realDllPath.replace(cores_pos, 7, "\\cores_real\\");
    } else {
        size_t dot_pos = realDllPath.find_last_of(".");
        if (dot_pos != std::string::npos) {
            realDllPath = realDllPath.substr(0, dot_pos) + "_real.dll";
        }
    }
    
    size_t slash_pos = dllPath.find_last_of("\\/");
    size_t ext_pos = dllPath.find_last_of(".");
    if (slash_pos != std::string::npos && ext_pos != std::string::npos && ext_pos > slash_pos) {
        current_core_name = dllPath.substr(slash_pos + 1, ext_pos - slash_pos - 1);
    }

    hRealCore = LoadLibraryA(realDllPath.c_str());
    if (!hRealCore) return;

    real_retro_init = (void(*)(void))GetProcAddress(hRealCore, "retro_init");
    real_retro_deinit = (void(*)(void))GetProcAddress(hRealCore, "retro_deinit");
    real_retro_api_version = (unsigned(*)(void))GetProcAddress(hRealCore, "retro_api_version");
    real_retro_get_system_info = (void(*)(struct retro_system_info*))GetProcAddress(hRealCore, "retro_get_system_info");
    real_retro_get_system_av_info = (void(*)(struct retro_system_av_info*))GetProcAddress(hRealCore, "retro_get_system_av_info");
    real_retro_set_environment = (void(*)(retro_environment_t))GetProcAddress(hRealCore, "retro_set_environment");
    real_retro_set_video_refresh = (void(*)(retro_video_refresh_t))GetProcAddress(hRealCore, "retro_set_video_refresh");
    real_retro_set_audio_sample = (void(*)(retro_audio_sample_t))GetProcAddress(hRealCore, "retro_set_audio_sample");
    real_retro_set_audio_sample_batch = (void(*)(retro_audio_sample_batch_t))GetProcAddress(hRealCore, "retro_set_audio_sample_batch");
    real_retro_set_input_poll = (void(*)(retro_input_poll_t))GetProcAddress(hRealCore, "retro_set_input_poll");
    real_retro_set_input_state = (void(*)(retro_input_state_t))GetProcAddress(hRealCore, "retro_set_input_state");
    real_retro_set_controller_port_device = (void(*)(unsigned, unsigned))GetProcAddress(hRealCore, "retro_set_controller_port_device");
    real_retro_reset = (void(*)(void))GetProcAddress(hRealCore, "retro_reset");
    real_retro_run = (void(*)(void))GetProcAddress(hRealCore, "retro_run");
    real_retro_serialize_size = (size_t(*)(void))GetProcAddress(hRealCore, "retro_serialize_size");
    real_retro_serialize = (bool(*)(void*, size_t))GetProcAddress(hRealCore, "retro_serialize");
    real_retro_unserialize = (bool(*)(const void*, size_t))GetProcAddress(hRealCore, "retro_unserialize");
    real_retro_cheat_reset = (void(*)(void))GetProcAddress(hRealCore, "retro_cheat_reset");
    real_retro_cheat_set = (void(*)(unsigned, bool, const char*))GetProcAddress(hRealCore, "retro_cheat_set");
    real_retro_load_game = (bool(*)(const struct retro_game_info*))GetProcAddress(hRealCore, "retro_load_game");
    real_retro_load_game_special = (bool(*)(unsigned, const struct retro_game_info*, size_t))GetProcAddress(hRealCore, "retro_load_game_special");
    real_retro_unload_game = (void(*)(void))GetProcAddress(hRealCore, "retro_unload_game");
    real_retro_get_region = (unsigned(*)(void))GetProcAddress(hRealCore, "retro_get_region");
    real_retro_get_memory_data = (void*(*)(unsigned))GetProcAddress(hRealCore, "retro_get_memory_data");
    real_retro_get_memory_size = (size_t(*)(unsigned))GetProcAddress(hRealCore, "retro_get_memory_size");
}

#define EXPORT extern "C" __declspec(dllexport)

EXPORT void retro_init(void) { InitProxy(); if(real_retro_init) real_retro_init(); }
EXPORT void retro_deinit(void) { 
    if(real_retro_deinit) real_retro_deinit(); 
    if (hPipe != INVALID_HANDLE_VALUE) { 
        CloseHandle(hPipe); 
        hPipe = INVALID_HANDLE_VALUE; 
    } 
}
EXPORT unsigned retro_api_version(void) { InitProxy(); return real_retro_api_version ? real_retro_api_version() : 1; }
EXPORT void retro_get_system_info(struct retro_system_info *info) { InitProxy(); if(real_retro_get_system_info) real_retro_get_system_info(info); }
EXPORT void retro_get_system_av_info(struct retro_system_av_info *info) { InitProxy(); if(real_retro_get_system_av_info) real_retro_get_system_av_info(info); }
EXPORT void retro_set_environment(retro_environment_t cb) { InitProxy(); if(real_retro_set_environment) real_retro_set_environment(cb); }
EXPORT void retro_set_video_refresh(retro_video_refresh_t cb) { if(real_retro_set_video_refresh) real_retro_set_video_refresh(cb); }
EXPORT void retro_set_audio_sample(retro_audio_sample_t cb) { if(real_retro_set_audio_sample) real_retro_set_audio_sample(cb); }
EXPORT void retro_set_audio_sample_batch(retro_audio_sample_batch_t cb) { if(real_retro_set_audio_sample_batch) real_retro_set_audio_sample_batch(cb); }
EXPORT void retro_set_input_poll(retro_input_poll_t cb) { if(real_retro_set_input_poll) real_retro_set_input_poll(cb); }
EXPORT void retro_set_input_state(retro_input_state_t cb) { if(real_retro_set_input_state) real_retro_set_input_state(cb); }
EXPORT void retro_set_controller_port_device(unsigned port, unsigned device) { if(real_retro_set_controller_port_device) real_retro_set_controller_port_device(port, device); }
EXPORT void retro_reset(void) { if(real_retro_reset) real_retro_reset(); }

EXPORT bool retro_load_game(const struct retro_game_info *game) {
    if(real_retro_load_game) {
        bool res = real_retro_load_game(game);
        if (res && game && game->path) {
            std::string path(game->path);
            std::string sys_name = "unknown";
            std::string rom_basename = path;

            size_t space_pos = path.find(" -rp ");
            if (space_pos != std::string::npos) {
                // Format MAME: "1944 -rp E:\RetroBat\roms\mame" ou "1944 -rp E:\RetroBat\roms\mame\1944.zip"
                rom_basename = path.substr(0, space_pos);
                size_t last_slash = path.find_last_of("\\/");
                if (last_slash != std::string::npos) {
                    sys_name = path.substr(last_slash + 1);
                    sys_name.erase(std::remove(sys_name.begin(), sys_name.end(), '\"'), sys_name.end());
                    
                    if (sys_name.find(".zip") != std::string::npos || sys_name.find(".7z") != std::string::npos) {
                        size_t prev_slash = path.find_last_of("\\/", last_slash - 1);
                        if (prev_slash != std::string::npos) {
                            sys_name = path.substr(prev_slash + 1, last_slash - prev_slash - 1);
                            sys_name.erase(std::remove(sys_name.begin(), sys_name.end(), '\"'), sys_name.end());
                        } else {
                            sys_name = "mame"; // Fallback ultime
                        }
                    }
                }
            } else {
                // Format Classique Console
                size_t last_slash = path.find_last_of("\\/");
                if (last_slash != std::string::npos) {
                    std::string filename = path.substr(last_slash + 1);
                    
                    size_t hash_pos = filename.find("#");
                    if (hash_pos != std::string::npos) filename = filename.substr(0, hash_pos);
                    
                    size_t dot_pos = filename.find_last_of(".");
                    if (dot_pos != std::string::npos) rom_basename = filename.substr(0, dot_pos);
                    else rom_basename = filename;
                    
                    size_t prev_slash = path.find_last_of("\\/", last_slash - 1);
                    if (prev_slash != std::string::npos) {
                        sys_name = path.substr(prev_slash + 1, last_slash - prev_slash - 1);
                        if (sys_name.find(".zip") != std::string::npos) {
                            // Protection contre l'extraction temporaire de Retroarch :
                            // Si path est ../roms/mame/1944.zip/1944
                            size_t prev2_slash = path.find_last_of("\\/", prev_slash - 1);
                            if (prev2_slash != std::string::npos) {
                                sys_name = path.substr(prev2_slash + 1, prev_slash - prev2_slash - 1);
                            } else {
                                sys_name = "mame"; // Fallback robuste pour l'arcade
                            }
                        }
                    }
                }
            }
            
            std::string rom_hash = "";
            if (game->data && game->size > 0) {
                rom_hash = MD5::compute((const uint8_t*)game->data, game->size);
            } else if (game->path) {
                std::ifstream f(game->path, std::ios::binary);
                if (f.is_open()) {
                    f.seekg(0, std::ios::end);
                    size_t size = (size_t)f.tellg();
                    f.seekg(0, std::ios::beg);
                    if (size > 0 && size < 256 * 1024 * 1024) { // 256MB limit to avoid OOM on large ISOs
                        std::vector<uint8_t> buffer(size);
                        f.read((char*)buffer.data(), size);
                        rom_hash = MD5::compute(buffer.data(), size);
                    }
                    f.close();
                }
            }

            current_game_name = rom_basename;
            g_alias_applied = "";
            g_mapping_error_logged = false;
            LoadMemFile(sys_name, rom_basename, rom_hash);
            ConnectPipe();
            
            if (hPipe != INVALID_HANDLE_VALUE) {
                std::stringstream init_log;
                if (!rom_hash.empty()) {
                    init_log << GetLogHeader() << "[DOF API] Signature MD5 detectee : " << rom_hash << "\n";
                }
                
                std::string game_basename = current_game_name;
                size_t ext_pos = game_basename.find_last_of(".");
                if (ext_pos != std::string::npos) game_basename = game_basename.substr(0, ext_pos);
                
                size_t hash_pos = game_basename.find("#");
                if (hash_pos != std::string::npos) game_basename = game_basename.substr(0, hash_pos);
                
                if (!g_alias_applied.empty()) {
                    init_log << GetLogHeader() << "[DOF API] Dictionnaire d'Alias applique: " << game_basename << " -> " << g_alias_applied << "\n";
                }
                init_log << GetLogHeader() << "[DOF API] Fichier MEM charge : " << g_last_mem_path << "\n";
                
                if (mem_watches.empty()) {
                    init_log << GetLogHeader() << "[!] Fichier MEM non trouve ou vide !\n";
                } else {
                    for (const auto& mw : mem_watches) {
                        if (mw.no_survey) continue;
                        init_log << GetLogHeader(mw.address, 0, true) << "[DOF API] LISTENING: [" << mw.category << "." << mw.event << "] -> " 
                                 << std::hex << std::uppercase << mw.address << std::nouppercase << std::dec;
                        
                        if (mw.condition == "eq" || mw.condition == "equal") {
                            init_log << " (==" << mw.condition_value << ")";
                        } else if (mw.condition == "neq" || mw.condition == "not_equal") {
                            init_log << " (!=" << mw.condition_value << ")";
                        } else if (mw.condition == "bit_true" || mw.condition == "bit_false") {
                            init_log << " (mask 0x" << std::hex << mw.mask << std::dec << ")";
                        }
                        
                        init_log << " [LOG:" << (mw.no_log ? "NO" : "YES") << ", SURVEY:" << (mw.no_survey ? "NO" : "YES") << "]";
                        init_log << " - " << mw.name << "\n";
                    }
                    init_log << GetLogHeader() << "[DOF API] Initialisation terminee, attente de la premiere frame...\n";
                }
                
                std::string initStr = init_log.str();
                DWORD bw; WriteFile(hPipe, initStr.c_str(), initStr.length(), &bw, NULL);
            }
        }
        return res;
    }
    return false;
}

EXPORT bool retro_load_game_special(unsigned game_type, const struct retro_game_info *info, size_t num_info) {
    if(real_retro_load_game_special) return real_retro_load_game_special(game_type, info, num_info);
    return false;
}

EXPORT void retro_unload_game(void) {
    current_game_name = "none";
    mem_watches.clear();
    if(real_retro_unload_game) real_retro_unload_game();
}

EXPORT unsigned retro_get_region(void) { return real_retro_get_region ? real_retro_get_region() : 0; }
EXPORT void *retro_get_memory_data(unsigned id) { return real_retro_get_memory_data ? real_retro_get_memory_data(id) : nullptr; }
EXPORT size_t retro_get_memory_size(unsigned id) { return real_retro_get_memory_size ? real_retro_get_memory_size(id) : 0; }
EXPORT size_t retro_serialize_size(void) { return real_retro_serialize_size ? real_retro_serialize_size() : 0; }
EXPORT bool retro_serialize(void *data, size_t size) { return real_retro_serialize ? real_retro_serialize(data, size) : false; }
EXPORT bool retro_unserialize(const void *data, size_t size) { return real_retro_unserialize ? real_retro_unserialize(data, size) : false; }
EXPORT void retro_cheat_reset(void) { if(real_retro_cheat_reset) real_retro_cheat_reset(); }
EXPORT void retro_cheat_set(unsigned index, bool enabled, const char *code) { if(real_retro_cheat_set) real_retro_cheat_set(index, enabled, code); }

EXPORT void retro_run(void) {
    if(real_retro_run) {
        real_retro_run();
        
        if (hPipe != INVALID_HANDLE_VALUE) {
            if (mem_watches.empty()) {
                if (!g_mapping_error_logged) {
                    std::stringstream ss;
                    ss << GetLogHeader() << "[DEBUG WRAPPER] ERREUR : Aucun mapping RAM actif !\n"
                       << "  - Jeu : " << current_game_name << "\n"
                       << "  - Chemin cherche : " << g_last_mem_path << "\n"
                       << "  - Raison probable : " << g_load_error << "\n";
                    std::string err = ss.str();
                    DWORD bytesWritten;
                    WriteFile(hPipe, err.c_str(), (DWORD)err.length(), &bytesWritten, NULL);
                    g_mapping_error_logged = true;
                }
            } else if (real_retro_get_memory_data) {
                uint8_t* system_ram = (uint8_t*)real_retro_get_memory_data(RETRO_MEMORY_SYSTEM_RAM);
                size_t ram_size = real_retro_get_memory_size(RETRO_MEMORY_SYSTEM_RAM);
                
                if (system_ram && ram_size > 0) {
                    for (auto& mw : mem_watches) {
                        if (mw.no_survey) continue;
                        unsigned int target_addr = mw.address % ram_size;

                        if (target_addr + mw.size <= ram_size) {
                            unsigned int current_val = 0;
                            bool do_swap = false; 
                            
                            if (mw.is_big_endian) {
                                for (unsigned int i = 0; i < mw.size; ++i) {
                                    unsigned int read_addr = target_addr + i;
                                    if (do_swap) read_addr ^= 1;
                                    current_val = (current_val << 8) | system_ram[read_addr];
                                }
                            } else {
                                for (unsigned int i = 0; i < mw.size; ++i) {
                                    unsigned int read_addr = target_addr + i;
                                    if (do_swap) read_addr ^= 1;
                                    current_val |= (system_ram[read_addr] << (i * 8));
                                }
                            }

                            if (mw.mask != 0xFFFFFFFF) current_val &= mw.mask;

                            bool trigger = false;
                            std::string cond = mw.condition;
                            
                            if (cond == "equal" || cond == "eq") {
                                if (current_val == mw.condition_value && (current_val != mw.last_value || !mw.initialized)) trigger = true;
                            }
                            else if (cond == "neq" || cond == "not_equal") {
                                if (current_val != mw.condition_value && (current_val != mw.last_value || !mw.initialized)) trigger = true;
                            }
                            else if (cond == "change") {
                                if (current_val != mw.last_value) {
                                    if (mw.condition_value == 0 || current_val == mw.condition_value) trigger = true;
                                }
                            }
                            else if (cond == "increase") {
                                if (mw.initialized && current_val > mw.last_value) trigger = true;
                            }
                            else if (cond == "decrease") {
                                if (mw.initialized && current_val < mw.last_value) trigger = true;
                            }
                            else if (cond == "bit_true") {
                                if ((current_val & mw.mask) == mw.mask && ((mw.last_value & mw.mask) != mw.mask || !mw.initialized)) trigger = true;
                            }
                            else if (cond == "bit_false") {
                                if ((current_val & mw.mask) == 0 && ((mw.last_value & mw.mask) != 0 || !mw.initialized)) trigger = true;
                            }

                            std::string act = mw.action;
                            bool v_no_log = false;
                            bool v_no_survey = false;
                            
                            if (!mw.raw_action_map.empty()) {
                                MappedResult mr = getMappedValueExt(mw.raw_action_map, current_val);
                                if (mr.found) {
                                    act = mr.action;
                                    v_no_log = mr.no_log;
                                    v_no_survey = mr.no_survey;
                                } else {
                                    trigger = false; 
                                }
                            }

                            if (mw.no_log || v_no_log || v_no_survey) trigger = false;
                            
                            mw.frames_since_change++;
                            
                            if (mw.is_permanently_muted) {
                                trigger = false;
                            } else if (trigger && !mw.is_score) {
                                unsigned int current_gap = mw.frames_since_change;
                                
                                // Si le delai entre deux changements est court (<= 20 frames, soit ~330ms, pour tolerer le lag interne de la SNES)
                                if (current_gap <= 20) {
                                    mw.regular_gap_count++;
                                    // S'il s'est repete "tres vite" 8 fois de suite sans jamais faire de vraie pause
                                    // C'est garantit que c'est un timer ou une boucle d'animation.
                                    if (mw.regular_gap_count >= 8) {
                                        mw.is_permanently_muted = true;
                                        trigger = false;
                                        
                                        if (hPipe != INVALID_HANDLE_VALUE) {
                                            std::stringstream alert;
                                            alert << GetLogHeader(mw.address, current_val, true) 
                                                  << "[DEBUG WRAPPER] WARNING: AUTO-MUTE DEFINITIF pour [" << mw.category << "." << mw.event << "] (" << mw.name << ") ! "
                                                  << "Signature de boucle infinie detectee. Blocage permanent.\n";
                                            std::string alertStr = alert.str();
                                            DWORD bwA; WriteFile(hPipe, alertStr.c_str(), (DWORD)alertStr.length(), &bwA, NULL);
                                        }
                                    }
                                } else {
                                    // Dès qu'il y a une pause "respire" (gap > 10), ce n'est plus une rafale infinie
                                    mw.regular_gap_count = 0;
                                }
                                mw.frames_since_change = 0;
                            }
                            
                            if (trigger) {
                                std::string payload_key = "ACTION";
                                if (act == "NEW_LEVEL" || act == "GAME_STATE" || mw.name.find("state") != std::string::npos || act.find("SCREEN") != std::string::npos) {
                                    payload_key = "STATE ";
                                } else if (act == "RING_GAIN" || act == "SCORE" || mw.is_score) {
                                    payload_key = "SCORE ";
                                }

                                std::stringstream msg;
                                MappedResult mr_desc = getMappedValueExt(mw.raw_map, current_val);
                                std::string mapped_str = mr_desc.found ? mr_desc.action : "";
                                
                                int delta = mw.initialized ? std::abs((int)current_val - (int)mw.last_value) : 0;
                                msg << GetLogHeader(mw.address, current_val, true) 
                                    << "[UDP_OUT] " << payload_key << ":" << act << " | SOURCE:" << mw.name << " | VALUE:" << std::dec << current_val << " | RATE:" << std::dec << delta;
                                if (!mapped_str.empty()) msg << "   (" << mw.name << " [" << mapped_str << "])";
                                msg << "\n";
                                
                                DWORD bw;
                                if (hPipe != INVALID_HANDLE_VALUE) {
                                    std::string msgStr = msg.str();
                                    WriteFile(hPipe, msgStr.c_str(), (DWORD)msgStr.length(), &bw, NULL);
                                }
                            }
                            mw.last_value = current_val;
                            mw.initialized = true;
                        } else {
                            static int oob_printed = 0;
                            if (oob_printed < 5) {
                                std::stringstream ss;
                                ss << GetLogHeader(target_addr, 0, true) << "[DEBUG WRAPPER] WARNING: Adresse HORS LIMITES! " << std::hex << target_addr << std::dec << " > ram_size (" << ram_size << ")\n";
                                std::string msgStr = ss.str();
                                DWORD bw; WriteFile(hPipe, msgStr.c_str(), (DWORD)msgStr.length(), &bw, NULL);
                                oob_printed++;
                            }
                        }
                    }
                } else {
                    static int debug_timer2 = 0;
                    if (debug_timer2++ % 60 == 0) {
                        std::stringstream err;
                        err << GetLogHeader() << "[DEBUG WRAPPER] ERREUR : System RAM pointer est NULL ou taille = 0 ! (ram_size = " << ram_size << ")\n";
                        std::string errStr = err.str();
                        DWORD bytesWritten;
                        WriteFile(hPipe, errStr.c_str(), (DWORD)errStr.length(), &bytesWritten, NULL);
                    }
                }
            } else {
                static int debug_timer3 = 0;
                if (debug_timer3++ % 60 == 0) {
                    std::stringstream err;
                    err << GetLogHeader() << "[DEBUG WRAPPER] ERREUR FATALE : real_retro_get_memory_data non disponible pour ce core !\n";
                    std::string errStr = err.str();
                    DWORD bytesWritten;
                    WriteFile(hPipe, errStr.c_str(), (DWORD)errStr.length(), &bytesWritten, NULL);
                }
            }
        }
    }
}
