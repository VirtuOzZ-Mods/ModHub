using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Peds;

/// <summary>Original registration generation, independent of third-party editors.</summary>
public static partial class PedMetadata
{
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SpawnPattern();

    public static void ValidateSpawnName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 63 || !SpawnPattern().IsMatch(name))
            throw new ArgumentException(L.T("core.peds.invalidTechnicalPedNameUse", name));
    }

    /// <summary>Read-only references may contain valid existing filename punctuation.
    /// They are hashed as supplied and are never used as new install paths.</summary>
    public static void ValidateReferencedSpawnName(string name)
    {
        if (
            string.IsNullOrEmpty(name)
            || name.Length > 128
            || name != name.Trim()
            || name is "." or ".."
            || name.Any(c => c < 0x20 || c > 0x7e || "\\/:*?\"<>|".Contains(c))
        )
            throw new ArgumentException(L.T("core.peds.invalidExistingPedModelName", name));
    }

    public static uint NameHash(string name)
    {
        uint hash = 0;
        unchecked
        {
            foreach (byte b in Encoding.ASCII.GetBytes(name.ToLowerInvariant()))
            {
                hash += b;
                hash += hash << 10;
                hash ^= hash >> 6;
            }
            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
        }
        return hash;
    }

    public static XDocument ParseDocument(string xml)
    {
        using var reader = XmlReader.Create(
            new StringReader(xml),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16L << 20,
            }
        );
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static XElement ParseItem(string xml)
    {
        var root = ParseDocument(xml).Root;
        if (root?.Name != "Item")
            throw new ArgumentException(L.T("core.peds.pedRegistrationMustContainOne"));
        var names = root.Elements("Name").ToList();
        if (names.Count != 1)
            throw new ArgumentException(L.T("core.peds.pedRegistrationRequiresOneName"));
        ValidateSpawnName(names[0].Value);
        return root;
    }

    public static string CreateDefault(string spawnName, PedGender gender, PedLayout layout)
    {
        ValidateSpawnName(spawnName);
        bool female = gender == PedGender.Female;
        string sex = female ? "female" : "male";
        static XElement V(string name, string value) => new(name, new XAttribute("value", value));
        // Full registration of a human model: the same set and order of fields (70) as an add-on character install
        // verified in game. An incomplete Item (without HDDist, PedVoiceGroup, component parameters…) and
        // PoseMatcherName "Female" crashed the game on load (a write to a null address in GTA5.exe).
        // The pose matcher for human models is "Male" for both genders, as in working registrations.
        var item = new XElement(
            "Item",
            new XElement("Name", spawnName),
            new XElement("PropsName", "null"),
            new XElement("ClipDictionaryName", female ? "move_f@generic" : "move_m@generic"),
            new XElement("BlendShapeFileName", "null"),
            new XElement("ExpressionSetName", "expr_set_ambient_" + sex),
            new XElement("ExpressionDictionaryName", "null"),
            new XElement("ExpressionName", "null"),
            new XElement("Pedtype", female ? "CIVFEMALE" : "CIVMALE"),
            new XElement("MovementClipSet", female ? "move_f@generic" : "move_m@generic"),
            new XElement("StrafeClipSet", "move_ped_strafing"),
            new XElement("MovementToStrafeClipSet", "move_ped_to_strafe"),
            new XElement("InjuredStrafeClipSet", "move_strafe_injured"),
            new XElement("FullBodyDamageClipSet", "dam_ko"),
            new XElement("AdditiveDamageClipSet", "dam_ad"),
            new XElement(
                "DefaultGestureClipSet",
                female ? "ANIM_GROUP_GESTURE_F_GENERIC" : "ANIM_GROUP_GESTURE_M_GENERIC"
            ),
            new XElement("FacialClipsetGroupName", "facial_clipset_group_gen_" + sex),
            new XElement("DefaultVisemeClipSet", female ? "ANIM_GROUP_VISEMES_F_LO" : "ANIM_GROUP_VISEMES_M_LO"),
            new XElement("SidestepClipSet", "CLIP_SET_ID_INVALID"),
            new XElement("PoseMatcherName", "Male"),
            new XElement("PoseMatcherProneName", "Male_prone"),
            new XElement("GetupSetHash", "NMBS_SLOW_GETUPS"),
            new XElement("CreatureMetadataName", "null"),
            new XElement("DecisionMakerName", "DEFAULT"),
            new XElement("MotionTaskDataSetName", "STANDARD_PED"),
            new XElement("DefaultTaskDataSetName", "STANDARD_PED"),
            new XElement("PedCapsuleName", female ? "STANDARD_FEMALE" : "STANDARD_MALE"),
            new XElement("PedLayoutName"),
            new XElement("PedComponentSetName"),
            new XElement("PedComponentClothName"),
            new XElement("PedIKSettingsName"),
            new XElement("TaskDataName"),
            V("IsStreamedGfx", layout == PedLayout.Streamed ? "true" : "false"),
            V("AmbulanceShouldRespondTo", "true"),
            V("CanRideBikeWithNoHelmet", "false"),
            V("CanSpawnInCar", "true"),
            V("IsHeadBlendPed", "false"),
            V("bOnlyBulkyItemVariations", "false"),
            new XElement("RelationshipGroup", female ? "CIVFEMALE" : "CIVMALE"),
            new XElement("NavCapabilitiesName", "STANDARD_PED"),
            new XElement("PerceptionInfo", "DEFAULT_PERCEPTION"),
            new XElement("DefaultBrawlingStyle", "BS_AI"),
            new XElement("DefaultUnarmedWeapon", "WEAPON_UNARMED"),
            new XElement("Personality", female ? "Streamed_Female" : "Streamed_Male"),
            new XElement("CombatInfo", "DEFAULT"),
            new XElement("VfxInfoName", "VFXPEDINFO_HUMAN_GENERIC"),
            new XElement("AmbientClipsForFlee", "FLEE"),
            new XElement("Radio1", "RADIO_GENRE_PUNK"),
            new XElement("Radio2", "RADIO_GENRE_JAZZ"),
            V("FUpOffset", "0.000000"),
            V("RUpOffset", "0.000000"),
            V("FFrontOffset", "0.000000"),
            V("RFrontOffset", "0.147000"),
            V("MinActivationImpulse", "20.000000"),
            V("Stubble", "0.000000"),
            V("HDDist", "3.000000"),
            V("TargetingThreatModifier", "1.000000"),
            V("KilledPerceptionRangeModifer", "-1.000000"),
            new XElement("Sexiness"),
            V("Age", "0"),
            V("MaxPassengersInCar", "0"),
            new XElement("ExternallyDrivenDOFs"),
            new XElement("PedVoiceGroup", "BAR_PERSON_PVG"),
            new XElement("AnimalAudioObject"),
            new XElement("AbilityType", "SAT_NONE"),
            new XElement("ThermalBehaviour", "TB_WARM"),
            new XElement("SuperlodType", "SLOD_HUMAN"),
            new XElement("ScenarioPopStreamingSlot", "SCENARIO_POP_STREAMING_NORMAL"),
            new XElement("DefaultSpawningPreference", "DSP_NORMAL"),
            V("DefaultRemoveRangeMultiplier", "1.000000"),
            V("AllowCloseSpawning", "false")
        );
        return item.ToString(SaveOptions.DisableFormatting);
    }
}
