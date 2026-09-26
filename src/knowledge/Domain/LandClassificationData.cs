using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ────────────────────────────────────────────────────────────────
// LandClassificationData.cs — Land classification lookup tables.
//
// Dual role since 2026-07-19:
//   1. BUILD-TIME ASSET — consumed by tools/extract_domain_data.py as a
//      generation input.
//   2. RUNTIME LOOKUP — served to the model via LandCodeLookupTool
//      (cc_lookup_land_code), so land-use name/code conversions query the
//      official tables instead of fabricating mappings (ADR 0044).
//
// Loads from land_classification.json when available; falls back to
// hardcoded inline data for offline/embedded scenarios.
//
// Data sources: MNR 2023-11 + GB/T 21010-2017
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Knowledge.Data
{
    public static class LandClassificationData
    {
        private static readonly Lazy<Dictionary<string, string>> _sdLazy = new(() =>
            LoadJsonDict("sd_code_to_name") ?? DefaultSdCodeToName!);
        private static readonly Lazy<Dictionary<string, string>> _yhLazy = new(() =>
            LoadJsonDict("yh_code_to_name") ?? DefaultYhCodeToName!);
        private static readonly Lazy<Dictionary<string, string[]>> _s2yLazy = new(() =>
            LoadJsonSdToYh() ?? DefaultSdToYh!);
        private static readonly Lazy<Dictionary<string, string>> _ysdmLazy = new(() =>
            LoadJsonDict("ysdm_codes") ?? DefaultYsdmCodes!);
        private static readonly Lazy<Dictionary<string, string>> _categorySqlLazy = new(() =>
            LoadJsonDict("category_sql") ?? DefaultCategorySql!);

        public static Dictionary<string, string> SdCodeToName => _sdLazy.Value!;
        public static Dictionary<string, string> YhCodeToName => _yhLazy.Value!;
        public static Dictionary<string, string[]> SdToYh => _s2yLazy.Value!;
        public static Dictionary<string, string> YsdmCodes => _ysdmLazy.Value!;
        public static Dictionary<string, string> SdCategorySql => _categorySqlLazy.Value!;

        private static JObject? LoadJson()
        {
            try
            {
                string? path = SpatialAgent.Runtime.Contracts.ResourcePaths.Find(Path.Combine("data", "land_classification.json"))
                    ?? SpatialAgent.Runtime.Contracts.ResourcePaths.Find("land_classification.json");
                if (path != null)
                    return JObject.Parse(File.ReadAllText(path));
            }
            catch { }
            return null;
        }

        private static Dictionary<string, string>? LoadJsonDict(string key)
        {
            var json = LoadJson();
            // 空对象视为"未填充"而非"覆盖为空"——否则空壳 JSON 会静默清空
            // 全部领域数据（2026-07-19 部署接通后由回归测试当场暴露）
            if (json?[key] is JObject obj && obj.Count > 0)
                return obj.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
            return null;
        }

        private static Dictionary<string, string[]>? LoadJsonSdToYh()
        {
            var json = LoadJson();
            if (json?["sd_to_yh"] is JObject obj && obj.Count > 0)
            {
                return obj.Properties().ToDictionary(
                    p => p.Name,
                    p => ((JArray)p.Value).Select(v => v.ToString()).ToArray());
            }
            return null;
        }

        // ── Default inline data (compile-time fallback) ──

        private static readonly Dictionary<string, string> DefaultSdCodeToName = new()
        {
            {"00","湿地"},{"0303","红树林地"},{"0304","森林沼泽"},{"0306","灌丛沼泽"},
            {"0402","沼泽草地"},{"0603","盐田"},{"1105","沿海滩涂"},{"1106","内陆滩涂"},
            {"1108","沼泽地"},{"01","耕地"},{"0101","水田"},{"0102","水浇地"},{"0103","旱地"},
            {"02","种植园用地"},{"0201","果园"},{"0201K","可调整果园"},{"0202","茶园"},
            {"0202K","可调整茶园"},{"0203","橡胶园"},{"0203K","可调整橡胶园"},
            {"0204","其他园地"},{"0204K","可调整其他园地"},{"03","林地"},
            {"0301","乔木林地"},{"0301K","可调整乔木林地"},{"0302","竹林地"},
            {"0302K","可调整竹林地"},{"0305","灌木林地"},{"0307","其他林地"},
            {"0307K","可调整其他林地"},{"04","草地"},{"0401","天然牧草地"},
            {"0403","人工牧草地"},{"0403K","可调整人工牧草地"},{"0404","其他草地"},
            {"05","商业服务业用地"},{"05H1","商业服务业设施用地"},{"0508","物流仓储用地"},
            {"06","工矿用地"},{"0601","工业用地"},{"0602","采矿用地"},
            {"07","住宅用地"},{"0701","城镇住宅用地"},{"0702","农村宅基地"},
            {"08","公共管理与公共服务用地"},{"08H1","机关团体新增闻出版用地"},
            {"08H2","科教文卫用地"},{"08H2A","高教用地"},{"0809","公用设施用地"},
            {"0810","公园与绿地"},{"0810A","广场用地"},{"09","特殊用地"},
            {"10","交通运输用地"},{"1001","铁路用地"},{"1002","轨道交通用地"},
            {"1003","公路用地"},{"1004","城镇村道路用地"},{"1005","交通服务场站用地"},
            {"1006","农村道路"},{"1007","机场用地"},{"1008","港口码头用地"},
            {"1009","管道运输用地"},{"11","水域及水利设施用地"},
            {"1101","河流水面"},{"1102","湖泊水面"},{"1103","水库水面"},
            {"1104","坑塘水面"},{"1104A","养殖坑塘"},{"1104K","可调整养殖坑塘"},
            {"1107","沟渠"},{"1107A","干渠"},{"1109","水工建筑用地"},
            {"1110","冰川及永久积雪"},{"12","其他土地"},{"1201","空闲地"},
            {"1202","设施农用地"},{"1203","田坎"},{"1204","盐碱地"},{"1205","沙地"},
            {"1206","裸土地"},{"1207","裸岩石砾地"},{"1208","后备耕地"},
            {"201","城市"},{"201A","城市独立工业仓储用地"},{"202","建制镇"},
            {"202A","建制镇"},{"203","村庄"},{"203A","村庄"},
            {"204","盐田及采矿用地"},{"205","特殊用地"},
            {"原来204","采矿用地"},{"原来205","风景名胜用地"}
        };

        // ───────────────────────────────────────────────────────────────────
        // Lookup 2: land-sea use code → name (2023 edition, 140 entries) — inline fallback
        // Source: MNR 2023-11 Land-Sea Use Classification Guide codes
        // ───────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> DefaultYhCodeToName = new()
        {
            {"01","耕地"},{"0101","水田"},{"0102","水浇地"},{"0103","旱地"},
            {"02","园地"},{"0201","果园"},{"0202","茶园"},{"0203","橡胶园地"},
            {"0204","油料园地"},{"0205","其他园地"},
            {"03","林地"},{"0301","乔木林地"},{"0302","竹林地"},
            {"0303","灌木林地"},{"0304","其他林地"},
            {"04","草地"},{"0401","天然牧草地"},{"0402","人工牧草地"},{"0403","其他草地"},
            {"05","湿地"},{"0501","森林沼泽"},{"0502","灌丛沼泽"},{"0503","沼泽草地"},
            {"0504","其他沼泽地"},{"0505","沿海滩涂"},{"0506","内陆滩涂"},{"0507","红树林地"},
            {"06","农业设施建设用地"},{"0601","农村道路"},{"060101","村道用地"},
            {"060102","田间道"},{"0602","设施农用地"},{"060201","种植设施建设用地"},
            {"060202","畜禽养殖设施建设用地"},{"060203","水产养殖设施建设用地"},
            {"07","居住用地"},{"0701","城镇住宅用地"},{"070101","一类城镇住宅用地"},
            {"070102","二类城镇住宅用地"},{"070103","三类城镇住宅用地"},
            {"0702","城镇社区服务设施用地"},{"0703","农村宅基地"},
            {"070301","一类农村宅基地"},{"070302","二类农村宅基地"},
            {"0704","农村社区服务设施用地"},
            {"08","公共管理与公共服务用地"},{"0801","机关团体用地"},{"0802","科研用地"},
            {"0803","文化用地"},{"080301","图书与展览用地"},{"080302","文化活动用地"},
            {"0804","教育用地"},{"080401","高等教育用地"},{"080402","中等职业教育用地"},
            {"080403","中小学用地"},{"080404","幼儿园用地"},{"080405","其他教育用地"},
            {"0805","体育用地"},{"080501","体育场馆用地"},{"080502","体育训练用地"},
            {"0806","医疗卫生用地"},{"080601","医院用地"},{"080602","基层医疗卫生设施用地"},
            {"080603","公共卫生用地"},{"0807","社会福利用地"},
            {"080701","老年人社会福利用地"},{"080702","儿童社会福利用地"},
            {"080703","残疾人社会福利用地"},{"080704","其他社会福利用地"},
            {"09","商业服务业用地"},{"0901","商业用地"},{"090101","零售商业用地"},
            {"090102","批发市场用地"},{"090103","餐饮用地"},{"090104","旅馆用地"},
            {"090105","公用设施营业网点用地"},{"0902","商务金融用地"},
            {"0903","娱乐用地"},{"0904","其他商业服务业用地"},
            {"10","工矿用地"},{"1001","工业用地"},{"100101","一类工业用地"},
            {"100102","二类工业用地"},{"100103","三类工业用地"},
            {"1002","采矿用地"},{"1003","盐田"},
            {"11","仓储用地"},{"1101","物流仓储用地"},{"110101","一类物流仓储用地"},
            {"110102","二类物流仓储用地"},{"110103","三类物流仓储用地"},{"1102","储备库用地"},
            {"12","交通运输用地"},{"1201","铁路用地"},{"1202","公路用地"},
            {"1203","机场用地"},{"1204","港口码头用地"},{"1205","管道运输用地"},
            {"1206","城市轨道交通用地"},{"1207","城镇村道路用地"},
            {"1208","交通场站用地"},{"120801","对外交通场站用地"},
            {"120802","公共交通场站用地"},{"120803","社会停车场用地"},
            {"1209","其他交通设施用地"},
            {"13","公用设施用地"},{"1301","供水用地"},{"1302","排水用地"},
            {"1303","供电用地"},{"1304","供燃气用地"},{"1305","供热用地"},
            {"1306","通信用地"},{"1307","邮政用地"},{"1308","广播电视设施用地"},
            {"1309","环卫用地"},{"1310","消防用地"},{"1311","水工设施用地"},
            {"1312","其他公用设施用地"},
            {"14","绿地与开敞空间用地"},{"1401","公园绿地"},{"1402","防护绿地"},
            {"1403","广场用地"},
            {"15","特殊用地"},{"1501","军事设施用地"},{"1502","使领馆用地"},
            {"1503","宗教用地"},{"1504","文物古迹用地"},{"1505","监教场所用地"},
            {"1506","殡葬用地"},{"1507","其他特殊用地"},
            {"16","留白用地"},
            {"17","陆地水域"},{"1701","河流水面"},{"1702","湖泊水面"},
            {"1703","水库水面"},{"1704","坑塘水面"},{"1705","沟渠"},
            {"1706","冰川及常年积雪"},
            {"18","渔业用海"},{"1801","渔业基础设施用海"},{"1802","增养殖用海"},
            {"1803","捕捞海域"},{"1804","农林牧业用岛"},
            {"19","工矿通信用海"},{"1901","工业用海"},{"1902","盐田用海"},
            {"1903","固体矿产用海"},{"1904","油气用海"},{"1905","可再生能源用海"},
            {"1906","海底电缆管道用海"},
            {"20","交通运输用海"},{"2001","港口用海"},{"2002","航运用海"},
            {"2003","路桥隧道用海"},{"2004","机场用海"},{"2005","其他交通运输用海"},
            {"21","游憩用海"},{"2101","风景旅游用海"},{"2102","文体休闲娱乐用海"},
            {"22","特殊用海"},{"2201","军事用海"},{"2202","科研教育用海"},
            {"2203","海洋保护修复及海岸防护工程用海"},{"2204","排污倾倒用海"},
            {"2205","水下文物保护用海"},{"2206","其他特殊用海"},
            {"23","其他土地"},{"2301","空闲地"},{"2302","后备耕地"},{"2303","田坎"},
            {"2304","盐碱地"},{"2305","沙地"},{"2306","裸土地"},{"2307","裸岩石砾地"},
            {"24","其他海域"}
        };

        // ───────────────────────────────────────────────────────────────────
        // Lookup 3: Third Survey → land-sea classification crosswalk (2023)
        //
        // Mapping rules:
        //   一对一：直接映射
        //   拆分：三调一个类别对应用地用海多个类别
        //   新增增：三调无对应，用地用海新增增
        //
        // Source: Land-Sea Use Classification Guide, Appendix F
        // ───────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string[]> DefaultSdToYh = new()
        {
            // Third Survey 00 wetland → land-sea use 05 wetland
            {"0303", new[]{"0507"}},   // mangrove
            {"0304", new[]{"0501"}},   // forest swamp
            {"0306", new[]{"0502"}},   // shrub swamp
            {"0402", new[]{"0503"}},   // marsh grassland
            {"0603", new[]{"1003"}},   // salt pan → industrial/mining land
            {"1105", new[]{"0505"}},   // coastal mudflat
            {"1106", new[]{"0506"}},   // inland mudflat
            {"1108", new[]{"0504"}},   // marshland
            // 01耕地 → 01耕地（不变）
            {"0101", new[]{"0101"}}, {"0102", new[]{"0102"}}, {"0103", new[]{"0103"}},
            // 02园地
            {"0201", new[]{"0201"}}, {"0202", new[]{"0202"}}, {"0203", new[]{"0203"}},
            {"0204", new[]{"0204","0205"}}, // Third Survey "other garden" → split into oil-bearing + other garden
            // 03林地
            {"0301", new[]{"0301"}}, {"0302", new[]{"0302"}},
            {"0305", new[]{"0303"}},   // shrubland (Third Survey 0305 → land-sea 0303)
            {"0307", new[]{"0304"}},   // other forest
            // 04草地
            {"0401", new[]{"0401"}}, {"0403", new[]{"0402"}}, {"0404", new[]{"0403"}},
            // 05商业服务业 → 拆分
            {"05H1", new[]{"0702","0704","0901","0902","0903","0904"}},
            {"0508", new[]{"1101","1102"}}, // logistics/warehouse → split
            // 06工矿用地
            {"0601", new[]{"1001"}}, {"0602", new[]{"1002"}},
            // 07住宅用地
            {"0701", new[]{"0701"}}, {"0702", new[]{"0703"}},
            // 08公共管理与公共服务 → 大幅拆分
            {"08H1", new[]{"0801"}}, {"08H2", new[]{"0802","0803","0804","0805","0806","0807","0702","0704"}},
            {"0809", new[]{"1301","1302","1303","1304","1305","1306","1307","1308","1309","1310","1312"}},
            {"0810", new[]{"1401","1402","1403"}},
            // 09特殊用地
            {"09", new[]{"1501","1502","1503","1504","1505","1506","1507"}},
            // 10交通运输
            {"1001", new[]{"1201","1208"}}, {"1002", new[]{"1206"}},
            {"1003", new[]{"1202"}}, {"1004", new[]{"1207"}},
            {"1005", new[]{"1208"}}, {"1006", new[]{"0601"}},
            {"1007", new[]{"1203"}}, {"1008", new[]{"1204"}},
            {"1009", new[]{"1205"}},
            // 11水域
            {"1101", new[]{"1701"}}, {"1102", new[]{"1702"}}, {"1103", new[]{"1703"}},
            {"1104", new[]{"1704"}}, {"1107", new[]{"1705"}},
            {"1109", new[]{"1311"}}, {"1110", new[]{"1706"}},
            // 12其他土地
            {"1201", new[]{"2301"}}, {"1202", new[]{"0602"}},
            {"1203", new[]{"2303"}}, {"1204", new[]{"2304"}},
            {"1205", new[]{"2305"}}, {"1206", new[]{"2306"}},
            {"1207", new[]{"2307"}},
        };

        // ───────────────────────────────────────────────────────────────────
        // Query methods
        // ───────────────────────────────────────────────────────────────────

        /// <summary>根据代码获取名称（先查用地用海，再查三调）</summary>
        public static string? GetName(string code)
        {
            if (YhCodeToName.TryGetValue(code, out var name)) return name;
            if (SdCodeToName.TryGetValue(code, out name)) return name;
            return null;
        }

        /// <summary>根据名称反向查代码（模糊匹配）</summary>
        public static string? FindCode(string name)
        {
            // Exact match
            foreach (var (code, n) in YhCodeToName)
                if (n == name) return code;
            foreach (var (code, n) in SdCodeToName)
                if (n == name) return code;
            // Fuzzy match
            foreach (var (code, n) in YhCodeToName)
                if (n.Contains(name) || name.Contains(n)) return code;
            foreach (var (code, n) in SdCodeToName)
                if (n.Contains(name) || name.Contains(n)) return code;
            return null;
        }

        /// <summary>获取代码层级：1=一级(2位), 2=二级(4位), 3=三级(6位)</summary>
        public static int GetLevel(string code)
        {
            var clean = code.TrimEnd('K', 'A');
            return clean.Length switch
            {
                <= 2 => 1,
                <= 4 => 2,
                _ => 3
            };
        }

        /// <summary>展开指定代码的所有直接子类</summary>
        public static List<string> GetChildren(string parentCode)
        {
            var clean = parentCode.TrimEnd('K', 'A');
            return YhCodeToName.Keys
                .Where(k =>
                {
                    var kc = k.TrimEnd('K', 'A');
                    return kc.StartsWith(clean) && kc != clean
                           && kc.Length == clean.Length + GetLevelStep(clean.Length);
                })
                .OrderBy(k => k)
                .ToList();
        }

        /// <summary>展开指定代码的所有子孙（所有层级）</summary>
        public static List<string> GetDescendants(string parentCode)
        {
            var clean = parentCode.TrimEnd('K', 'A');
            return YhCodeToName.Keys
                .Where(k => k.TrimEnd('K', 'A').StartsWith(clean) && k.TrimEnd('K', 'A') != clean)
                .OrderBy(k => k)
                .ToList();
        }

        /// <summary>获取父类代码</summary>
        public static string? GetParent(string code)
        {
            var clean = code.TrimEnd('K', 'A');
            return clean.Length switch
            {
                <= 2 => null,                    // level-1 class has no parent
                <= 4 => clean[..2],               // level-2 → level-1 (2 digits)
                _ => clean[..4]                   // level-3 → level-2 (4 digits)
            };
        }

        /// <summary>三调代码→用地用海代码</summary>
        public static string[] ConvertSdToYh(string sdCode)
        {
            return SdToYh.TryGetValue(sdCode, out var yhCodes)
                ? yhCodes
                : System.Array.Empty<string>();
        }

        /// <summary>用地用海代码→三调代码（反向查找）</summary>
        public static string? ConvertYhToSd(string yhCode)
        {
            foreach (var (sd, yhList) in SdToYh)
                if (yhList.Contains(yhCode))
                    return sd;
            return null;
        }

        /// <summary>获取一级类代码列表（24个）</summary>
        public static List<string> GetLevel1Codes()
        {
            return YhCodeToName.Keys
                .Where(k => k.Length == 2)
                .OrderBy(k => k)
                .ToList();
        }

        /// <summary>三调三大类(农用地/建设用地/未利用地)判断</summary>
        public static string GetSdCategory(string sdCode)
        {
            var clean = sdCode.TrimEnd('K', 'A');
            // Agricultural: 01-04 + partial 05/11/12
            if (clean.StartsWith("01") || clean.StartsWith("02") ||
                clean.StartsWith("03") || clean.StartsWith("04"))
                return "农用地";
            // Construction: 05-10 + partial 11/12
            if (clean.StartsWith("05") || clean.StartsWith("06") ||
                clean.StartsWith("07") || clean.StartsWith("08") ||
                clean.StartsWith("09") || clean.StartsWith("10"))
                return "建设用地";
            // Reservoirs/ponds/ditches → agricultural; rest → construction or unused
            if (clean == "1103" || clean == "1104" || clean == "1107")
                return "农用地";
            if (clean == "1109" || clean == "1001" || clean == "1003" ||
                clean == "1004" || clean == "1005" || clean == "1006" ||
                clean == "1007" || clean == "1008" || clean == "1009")
                return "建设用地";
            // Other land
            if (clean.StartsWith("12"))
                return clean switch { "1203" => "农用地", "1202" => "建设用地", _ => "未利用地" };
            return "未利用地";
        }

        // ───────────────────────────────────────────────────────────────────
        // Helpers
        // ───────────────────────────────────────────────────────────────────

        private static int GetLevelStep(int currentLen)
        {
            return currentLen switch { 2 => 2, 4 => 2, _ => 0 };
        }

        /// <summary>三调条目总数</summary>
        public static int SdCount => SdCodeToName.Count;

        /// <summary>用地用海条目总数</summary>
        public static int YhCount => YhCodeToName.Count;

        /// <summary>三调→用地用海映射条目数</summary>
        public static int SdToYhCount => SdToYh.Count;

        // ───────────────────────────────────────────────────────────────────
        // Third Survey 3-category SQL filters
        // Source: Third Survey to Land-Sea category mapping rules
        // For precise agricultural/construction/unused classification
        // ───────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> DefaultCategorySql = new()
        {
            {"GDMJ", "DLBM IN ('0101','0102','0103')"},
            {"LDMJ", "DLBM IN ('0301','0301K','0302','0302K','0303','0304','0305','0306','0307','0307K')"},
            {"CDMJ", "DLBM IN ('0401','0403','0403K','0404')"},
            {"YDMJ", "DLBM IN ('0201','0201K','0202','0202K','0203','0203K','0204','0204K')"},
            {"QTYDMJ", "DLBM IN ('1006','1103','1104','1104A','1104K','1107','1107A','1202','1203','0402')"},
            {"JSYDMJ", "DLBM IN ('05H1','0508','0601','0602','0603','0701','0702','08H1','08H2','08H2A','0809','0810','0810A','09','1001','1002','1003','1004','1005','1007','1008','1009','1109','1201')"},
            {"WLYDMJ", "DLBM IN ('1101','1102','1105','1106','1108','1110','1204','1205','1206','1207')"},
            {"NYDMJ", "DLBM IN ('0404','0101','0102','0103','0201','0201K','0202','0202K','0203','0203K','0204','0204K','0301','0301K','0302','0302K','0303','0304','0305','0306','0307','0307K','0401','0402','0403','0403K','1006','1103','1104','1104A','1104K','1107','1107A','1202','1203')"}
        };

        public static readonly Dictionary<string, string> SdCategoryNames = new()
        {
            {"GDMJ","耕地面积"},{"LDMJ","林地面积"},{"CDMJ","草地面积"},
            {"YDMJ","园地面积"},{"QTYDMJ","其他园地面积"},
            {"JSYDMJ","建设用地面积"},{"WLYDMJ","未利用地面积"},{"NYDMJ","农用地面积"}
        };

        // ───────────────────────────────────────────────────────────────────
        // Construction land / other land level-1 codes
        // Source: Land-Sea Use Classification (2023) mapping rules
        // HH = 混合用地（Mixed-use）
        // ───────────────────────────────────────────────────────────────────

        /// <summary>建设用地一级类代码（含混合用地HH）</summary>
        public static readonly List<string> ConstructionLandCodes = new()
            { "07", "08", "HH", "09", "10", "11", "12", "13", "14", "15", "16" };

        /// <summary>其他用地（非建设用地）一级类代码</summary>
        public static readonly List<string> OtherLandCodes = new()
            { "01", "02", "03", "04", "05", "06", "17", "18", "19", "20", "21", "22", "23", "24" };

        /// <summary>建设用地下属一级类（用于占比计算）</summary>
        public static readonly List<string> ConstructionSubCodes = new()
            { "07", "08", "HH", "09", "10", "11", "12", "13", "14", "15", "16", "建设用地小计" };

        /// <summary>判断是否为建设用地一级类</summary>
        public static bool IsConstructionLand(string level1Code)
            => ConstructionLandCodes.Contains(level1Code);

        /// <summary>判断是否为其他用地一级类</summary>
        public static bool IsOtherLand(string level1Code)
            => OtherLandCodes.Contains(level1Code);

        // ───────────────────────────────────────────────────────────────────
        // YSDM 要素代码映射
        // Source: National Spatial Planning Database Specification 硬编码 + 数据库规范 表1
        // YSDM field values when creating new feature classes
        // ───────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> DefaultYsdmCodes = new()
        {
            {"市级行政区", "1000640100"},
            {"县级行政区", "1000650100"},
            {"乡镇级行政区", "1000660100"},
            {"村级调查区", "1000600400"},
            {"村级调查区界线", "1000600500"},
            {"规划地类图斑", "2003020210"},
            {"规划用地", "2003010100"},
            {"现状用地", "2003010800"},
            {"生态保护红线", "2090020211"},
            {"永久基本农田", "2090020212"},
            {"城镇开发边界", "2090020213"},
            {"历史文化保护区", "2003020910"},
            {"管控边界", "2003020710"},
            {"自然保护地", "2090020300"},
            {"规划分区", "2090020200"},
            {"生态保护重要性评价结果", "2080050100"},
            {"农业生产适宜性评价结果", "2080050200"},
            {"城镇建设适宜性评价结果", "2080050300"},
            {"现状用地用海", "2080040201"},
        };

        /// <summary>根据图层名称获取 YSDM</summary>
        public static string? GetYsdm(string layerName)
        {
            if (YsdmCodes.TryGetValue(layerName, out var code)) return code;
            foreach (var (key, val) in YsdmCodes)
                if (layerName.Contains(key) || key.Contains(layerName))
                    return val;
            return null;
        }

        // ───────────────────────────────────────────────────────────────────
        // Heritage protection level + type codes
        // Source: National Spatial Planning Database Specification lines 1085-1096
        // ───────────────────────────────────────────────────────────────────

        public static readonly Dictionary<string, string> HeritageLevelCodes = new()
        {
            {"10", "国家"}, {"20", "省"}, {"30", "市"}, {"40", "县"}, {"50", "其他"}
        };

        public static readonly Dictionary<string, string> HeritageTypeCodes = new()
        {
            {"01", "历史文化核心保护范围"},
            {"02", "历史文化建设控制地带范围"},
            {"03", "其他"}
        };

        public static readonly Dictionary<string, string> HeritageResourceCategories = new()
        {
            {"11", "世界遗产"}, {"12", "国家文化公园"},
            {"21", "历史文化名城"}, {"22", "历史文化名镇"},
            {"23", "历史文化名村"}, {"24", "历史文化街区"},
            {"25", "历史建筑"},
            {"31", "文物保护单位"}, {"32", "传统村落"}
        };

        // ───────────────────────────────────────────────────────────────────
        // Village type classification
        // Source: National Spatial Planning Database Specification lines 246-247
        // ───────────────────────────────────────────────────────────────────

        public static readonly List<string> VillageTypeKeywords = new()
            { "集聚", "提升", "中心", "转型", "融合", "城郊", "保护", "开发", "特色", "搬迁", "撤并", "衰退" };

        public static readonly List<string> VillageTypeFullNames = new()
            { "集聚提升中心", "转型融合城郊", "保护开发特色", "搬迁撤并衰退" };

        /// <summary>根据关键字匹配村庄类型</summary>
        public static string? MatchVillageType(string input)
        {
            foreach (var full in VillageTypeFullNames)
                if (input.Contains(full)) return full;

            var matched = VillageTypeKeywords.Where(k => input.Contains(k)).ToList();
            return matched.Count switch
            {
                0 => null,
                1 => VillageTypeFullNames.FirstOrDefault(f => f.Contains(matched[0])),
                _ => VillageTypeFullNames.FirstOrDefault(f =>
                    matched.All(k => f.Contains(k))) ?? VillageTypeFullNames[0]
            };
        }

        // ───────────────────────────────────────────────────────────────────
        // Administrative level keywords
        // Source: National Spatial Planning Database Specification lines 122-123
        // ───────────────────────────────────────────────────────────────────

        public static readonly List<string> CityLevelWords = new()
            { "市", "县", "实验区", "区" };

        public static readonly List<string> TownLevelWords = new()
            { "镇", "乡", "街道", "片区" };

        // ───────────────────────────────────────────────────────────────────
        // Construction land mixed types (for development zone merging)
        // Source: National Spatial Planning Database Specification line 892
        // ───────────────────────────────────────────────────────────────────

        public static readonly List<string> ConstructionLandTypeNames = new()
        {
            "居住用地", "公共管理与公共服务用地", "商业服务业用地",
            "工业用地", "仓储用地", "城镇村道路用地", "交通场站用地",
            "其他交通设施用地", "公用设施用地", "绿地与开敞空间用地",
            "留白用地", "空闲地"
        };
    }
}
