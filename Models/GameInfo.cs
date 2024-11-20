using System.Text.Json.Serialization;

namespace DecryptStation3.Models
{
    public class GameInfo
    {
        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = "";

        [JsonPropertyName("sha1")]
        public string Sha1 { get; set; } = "";

        [JsonPropertyName("hex_key")]
        public string HexKey { get; set; } = "";
    }
}