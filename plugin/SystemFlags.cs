// 由 known_unknown.py --system-flags 產生，別手改。遊戲更新後重跑。
namespace KnownUnknown
{
    internal static class SystemFlags
    {
        // 產生時的遊戲版本(Application.version);插件啟動時對不上就警告
        public const string GameVersion = "release_1.0.6001.0";

        // Lua 以外(戰鬥、商店、設施、任務…)會寫入的旗標
        public static readonly string[] Names =
        {
            "A00001",
            "A00002",
            "A00003",
            "A00004",
            "Alpha_001",
            "B00001",
            "B00002",
            "B00003",
            "B00004",
            "BattleProgress_01",
            "BattleProgress_02",
            "BattleProgress_03",
            "BattleResult_001",
            "BattleResult_002",
            "C00000",
            "C00001",
            "C00002",
            "C00003",
            "C00004",
            "C00005",
            "Demo",
            "FacilityAdd_001_01",
            "FacilityAdd_002_01",
            "FacilityAdd_002_02",
            "Facility_001",
            "Facility_002",
            "Facility_003",
            "Facility_004",
            "Facility_005",
            "Facility_006",
            "H00001",
            "H00002",
            "H00003",
            "H00004",
            "H00005",
            "H00006",
            "H01001",
            "H01002",
            "H02001",
            "H02002",
            "M20047",
            "M50044",
            "M80041",
            "NewGamePlus",
            "Pinball_Broken",
            "Pinball_Broken_Count",
            "R0001",
            "R0002",
            "S9001",
            "S9200",
            "Score_01",
            "Shop_01",
            "Shop_02",
            "T0002",
            "TC0001",
            "TC0002",
            "TC0003",
            "TC0004",
            "TC0005",
            "TC0006",
            "TC0009",
            "TC0012",
            "Talk_Brother3",
            "Talk_Brother4",
            "Talk_Option_01",
            "Talk_Option_02",
            "Talk_Option_03",
            "Talk_Option_04",
            "Talk_Option_05",
            "Talk_Option_06",
            "Talk_Option_07",
            "Talk_Option_08",
            "Talk_Option_09",
            "Talk_Option_10",
            "Talk_Option_11",
            "Talk_Option_12",
            "Z00002",
        };

        // Lua 以外(新一輪繼承、商店、戰鬥…)會改好感的角色(RelationshipStatType)
        public static readonly int[] Relationships =
        {
            0,  // Relationship_Sister1_小師妹
            1,  // Relationship_Brother1
            2,  // Relationship_Brother2
            3,  // Relationship_Brother3
            4,  // Relationship_Brother4
            5,  // Relationship_Master
            7,  // Relationship_Special3_葉雲舟
            8,  // Relationship_Girl2_葉雲裳
            9,  // Relationship_Special4_樊嘯天
            12,  // Relationship_Girl4_龍湘
            13,  // Relationship_Special2_龍淵
            102,  // Relationship_Special102_南宮深
            103,  // Relationship_Special103_南宮淺
            205,  // Relationship_Special205_尹志平
            206,  // Relationship_Special206_福韞
            401,  // Relationship_Special401_王二壯
            404,  // Relationship_Special404_丹霞子
            405,  // Relationship_Special405_申屠龍
            605,  // Relationship_Girl5_虞小梅
            606,  // Relationship_Girl6_夏侯蘭
            607,  // Relationship_Girl7_郁竹
            608,  // Relationship_Girl8_魏菊
            609,  // Relationship_Girl9_上官螢
            800,  // Relationship_Special800_宋悲
            808,  // Relationship_Special808_解無塵
            809,  // Relationship_Special809_李富貴
            999,  // Relationship_Special999_瑞笙
        };
    }
}
