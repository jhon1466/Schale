using System.Text.Json.Serialization;

namespace Schale_Server.Models.SDK
{
    public class CountryV2Response
    {
        [JsonPropertyName("ip")]
        public string IP;
        
        [JsonPropertyName("country-code")]
        public string CountryCode;
    }
}
