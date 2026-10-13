using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentsBench.Infrastructure.Partner;

/// <summary>Enums travel as their names on the wire; a number would silently survive a reordering.</summary>
internal static class PartnerJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
