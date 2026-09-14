using System.Globalization;
using System.Text.Json;

namespace AIMaster.Services;

internal static class JsonProtocolValue
{
    public static bool TryGetInt32(JsonElement value, out int number)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out number);
        if (value.ValueKind == JsonValueKind.String)
            return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

        number = 0;
        return false;
    }

    public static bool TryGetInt64(JsonElement value, out long number)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out number);
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

        number = 0;
        return false;
    }

    public static bool TryGetDouble(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out number);
        if (value.ValueKind == JsonValueKind.String)
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                   double.IsFinite(number);

        number = 0;
        return false;
    }
}
