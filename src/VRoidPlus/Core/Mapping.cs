using System.IO;

namespace VRoidPlus
{
    /// <summary>
    /// Where VRoid Studio keeps its editor data. VRoid's internal class names are not public and change
    /// between versions, so the two features that edit VRoid's own data (face sets and hair merging)
    /// read the locations from BepInEx/config/VRoidPlus/mapping.json instead of hard-coding them.
    ///
    /// Path syntax (see ReflectionPath):
    ///   "Some.Namespace.Type::StaticMember.Member.List[0].Method()"
    ///   "$find:Some.Namespace.SomeMonoBehaviour.Member"   (FindObjectOfType, then members)
    /// </summary>
    public class MappingData
    {
        public string Comment = "Fill these in with the Explorer tab in the VRoid Plus window. See README.md, 'Mapping'.";

        /// <summary>Object holding every face selection and slider of the avatar being edited.</summary>
        public string FaceData = "";
        /// <summary>Optional: property names inside FaceData to skip (e.g. ids that must not be overwritten).</summary>
        public string[] FaceExclude = { };
        /// <summary>Optional method path called after a face set is applied, so VRoid rebuilds the face.</summary>
        public string FaceRefresh = "";

        /// <summary>The list of hair groups in the hairstyle currently open in the hairstyle editor.</summary>
        public string HairGroupList = "";
        /// <summary>Property names inside a hair group that hold unique ids; they are regenerated on import.</summary>
        public string[] HairIdProperties = { "Id", "ID", "Guid", "GUID", "Uuid", "id", "guid", "uuid" };
        /// <summary>Optional method path called after groups are added, so VRoid regenerates the hair mesh.</summary>
        public string HairRefresh = "";

        /// <summary>Renderer/object name fragments used to recognise hair and accessories in the scene.</summary>
        public string[] HairNamePatterns = { "Hair", "hair", "HAIR" };
        public string[] AccessoryNamePatterns = { "Accessory", "accessory", "Acc_", "acc_", "Accessories" };
    }

    internal static class Mapping
    {
        public static MappingData Current = new();
        public static string FilePath => Path.Combine(Plugin.DataDir, "mapping.json");

        public static void Load()
        {
            if (!File.Exists(FilePath)) Json.Write(FilePath, new MappingData());
            Current = Json.Read<MappingData>(FilePath);
        }

        public static void Save() => Safe.Run("Mapping.Save", () => Json.Write(FilePath, Current));
    }
}
