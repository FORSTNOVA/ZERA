using System;
using System.Collections.Generic;
using System.Text.Json;

// Keep the existing provider parsers shared with net48. Their object model expects
// dictionaries and object arrays, rather than JsonElement values.
namespace System.Web.Script.Serialization
{
    public sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = 2097152;
        public int RecursionLimit { get; set; } = 100;
        private JsonSerializerOptions Options => new JsonSerializerOptions {
            IncludeFields = true, PropertyNameCaseInsensitive = true, MaxDepth = RecursionLimit
        };
        public string Serialize(object value)
        {
            string result = JsonSerializer.Serialize(value, Options);
            if (result.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds configured size limit.");
            return result;
        }
        public T Deserialize<T>(string text)
        {
            if (typeof(T) == typeof(Dictionary<string, object>) || typeof(T) == typeof(object))
                return (T)DeserializeObject(text);
            Validate(text);
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        public object DeserializeObject(string text)
        {
            Validate(text);
            using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = RecursionLimit }))
                return ConvertValue(doc.RootElement);
        }
        private void Validate(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds configured size limit.");
        }
        private static object ConvertValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>();
                    foreach (var p in value.EnumerateObject()) map[p.Name] = ConvertValue(p.Value);
                    return map;
                case JsonValueKind.Array:
                    var items = new List<object>();
                    foreach (var item in value.EnumerateArray()) items.Add(ConvertValue(item));
                    return items.ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number:
                    if (value.TryGetInt32(out int i)) return i;
                    if (value.TryGetInt64(out long l)) return l;
                    if (value.TryGetDecimal(out decimal d)) return d;
                    return value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
