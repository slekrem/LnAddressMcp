using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();
var app = builder.Build();

app.MapMcp("ln-address");

await app.RunAsync();

[McpServerToolType]
public static class LnAddressTools
{
    [McpServerTool, Description("Creates a Lightning Network invoice (payment request) from a Lightning Address. Returns a BOLT11 invoice string that can be used to receive Bitcoin payments over the Lightning Network.")]
    public static async Task<string> CreateInvoice(
        [Description("Lightning Address in email format (e.g., 'alice@wallet.com', 'bob@zaphq.io'). This is the recipient's Lightning Address where the payment will be sent.")]string address,
        [Description("Payment amount in satoshis (1 BTC = 100,000,000 satoshis). Must be within the recipient's min/max limits. Example: 1000 for 0.00001000 BTC.")]int amount)
    {
        try
        {
            using var client = new HttpClient();
            
            // Parse lightning address
            var parts = address.Split('@');
            if (parts.Length != 2)
            {
                return "Error: Invalid lightning address format. Expected: user@domain.com";
            }
            
            var username = parts[0];
            var domain = parts[1];
            
            // Step 1: Get LNURL Pay endpoint
            var lnurlEndpoint = $"https://{domain}/.well-known/lnurlp/{username}";
            var lnurlResponse = await client.GetAsync(lnurlEndpoint);
            
            if (!lnurlResponse.IsSuccessStatusCode)
            {
                return $"Error: Failed to resolve lightning address. Status: {lnurlResponse.StatusCode}";
            }
            
            var lnurlContent = await lnurlResponse.Content.ReadAsStringAsync();
            var lnurlData = JsonSerializer.Deserialize<LnurlPayResponse>(lnurlContent);
            
            if (lnurlData == null || lnurlData.Tag != "payRequest")
            {
                return "Error: Invalid LNURL Pay response";
            }
            
            // Convert satoshis to millisatoshis
            var amountMsat = amount * 1000;
            
            // Check amount limits
            if (amountMsat < lnurlData.MinSendable || amountMsat > lnurlData.MaxSendable)
            {
                return $"Error: Amount {amount} sats is outside allowed range ({lnurlData.MinSendable / 1000}-{lnurlData.MaxSendable / 1000} sats)";
            }
            
            // Step 2: Request invoice from callback
            var callbackUrl = $"{lnurlData.Callback}?amount={amountMsat}&nonce={Guid.NewGuid()}";
            var invoiceResponse = await client.GetAsync(callbackUrl);
            
            if (!invoiceResponse.IsSuccessStatusCode)
            {
                return $"Error: Failed to create invoice. Status: {invoiceResponse.StatusCode}";
            }
            
            var invoiceContent = await invoiceResponse.Content.ReadAsStringAsync();
            var invoiceData = JsonSerializer.Deserialize<LnurlPayCallbackResponse>(invoiceContent);
            
            if (invoiceData?.Status == "ERROR")
            {
                return $"Error: {invoiceData.Reason}";
            }
            
            return invoiceData?.Pr ?? "Error: No invoice received";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

public class LnurlPayResponse
{
    [JsonPropertyName("callback")]
    public string Callback { get; set; } = "";
    [JsonPropertyName("maxSendable")]
    public long MaxSendable { get; set; }
    [JsonPropertyName("minSendable")]
    public long MinSendable { get; set; }
    [JsonPropertyName("metadata")]
    public string Metadata { get; set; } = "";
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";
    [JsonPropertyName("commentAllowed")]
    public int CommentAllowed { get; set; }
}

public class LnurlPayCallbackResponse
{
    [JsonPropertyName("pr")]
    public string? Pr { get; set; }
    [JsonPropertyName("status")]
    public string? Status { get; set; }
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
