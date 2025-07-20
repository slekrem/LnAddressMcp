using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();

app.UsePathBase("/ln-address")
    .UseDefaultFiles()
    .UseStaticFiles();

app.MapMcp("/mcp");

await app.RunAsync();

[McpServerToolType]
public static class LnAddressTools
{
    private static readonly ILogger _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("LnAddressTools");

    [McpServerTool, Description("Creates a Lightning Network invoice (payment request) from a Lightning Address. Returns a BOLT11 invoice string that can be used to receive Bitcoin payments over the Lightning Network.")]
    public static async Task<string> CreateInvoice(
        [Description("Lightning Address in email format (e.g., 'alice@wallet.com', 'bob@zaphq.io'). This is the recipient's Lightning Address where the payment will be sent.")] string address,
        [Description("Payment amount in satoshis (1 BTC = 100,000,000 satoshis). Must be within the recipient's min/max limits. Example: 1000 for 0.00001000 BTC.")] int amount)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["LightningAddress"] = address,
            ["AmountSats"] = amount,
            ["RequestId"] = Guid.NewGuid()
        });

        _logger.LogInformation($"Starting invoice creation for {address} with amount {amount} sats");

        try
        {
            using var client = new HttpClient();

            // Parse lightning address
            var parts = address.Split('@');
            if (parts.Length != 2)
            {
                _logger.LogError($"Invalid lightning address format: {address}");
                return "Error: Invalid lightning address format. Expected: user@domain.com";
            }

            var username = parts[0];
            var domain = parts[1];

            _logger.LogDebug($"Parsed lightning address - Username: {username}, Domain: {domain}");

            // Step 1: Get LNURL Pay endpoint
            var lnurlEndpoint = $"https://{domain}/.well-known/lnurlp/{username}";
            _logger.LogDebug($"Requesting LNURL endpoint: {lnurlEndpoint}");

            var lnurlResponse = await client.GetAsync(lnurlEndpoint);

            if (!lnurlResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to resolve lightning address. Status: {lnurlResponse.StatusCode}, Endpoint: {lnurlEndpoint}");
                return $"Error: Failed to resolve lightning address. Status: {lnurlResponse.StatusCode}";
            }

            var lnurlContent = await lnurlResponse.Content.ReadAsStringAsync();
            _logger.LogDebug($"LNURL response received: {lnurlContent}");

            var lnurlData = JsonSerializer.Deserialize<LnurlPayResponse>(lnurlContent);

            if (lnurlData == null || lnurlData.Tag != "payRequest")
            {
                _logger.LogError($"Invalid LNURL Pay response. Tag: {lnurlData?.Tag}");
                return "Error: Invalid LNURL Pay response";
            }

            // Convert satoshis to millisatoshis
            var amountMsat = amount * 1000;

            _logger.LogDebug($"Amount limits - Min: {lnurlData.MinSendable} msat, Max: {lnurlData.MaxSendable} msat, Requested: {amountMsat} msat");

            // Check amount limits
            if (amountMsat < lnurlData.MinSendable || amountMsat > lnurlData.MaxSendable)
            {
                _logger.LogWarning($"Amount {amount} sats is outside allowed range ({lnurlData.MinSendable / 1000}-{lnurlData.MaxSendable / 1000} sats)");
                return $"Error: Amount {amount} sats is outside allowed range ({lnurlData.MinSendable / 1000}-{lnurlData.MaxSendable / 1000} sats)";
            }

            // Step 2: Request invoice from callback
            var callbackUrl = $"{lnurlData.Callback}?amount={amountMsat}&nonce={Guid.NewGuid()}";
            _logger.LogDebug($"Requesting invoice from callback: {callbackUrl}");

            var invoiceResponse = await client.GetAsync(callbackUrl);

            if (!invoiceResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to create invoice. Status: {invoiceResponse.StatusCode}, Callback: {callbackUrl}");
                return $"Error: Failed to create invoice. Status: {invoiceResponse.StatusCode}";
            }

            var invoiceContent = await invoiceResponse.Content.ReadAsStringAsync();
            _logger.LogDebug($"Invoice response received: {invoiceContent}");

            var invoiceData = JsonSerializer.Deserialize<LnurlPayCallbackResponse>(invoiceContent);

            if (invoiceData?.Status == "ERROR")
            {
                _logger.LogError($"Invoice creation failed with error: {invoiceData.Reason}");
                return $"Error: {invoiceData.Reason}";
            }

            if (string.IsNullOrEmpty(invoiceData?.Pr))
            {
                _logger.LogError("No invoice received in response");
                return "Error: No invoice received";
            }

            _logger.LogInformation($"Successfully created invoice for {address} with amount {amount} sats");
            _logger.LogDebug($"Generated invoice: {invoiceData.Pr}");

            return invoiceData.Pr;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception occurred while creating invoice for {address}");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Validates a Lightning Address and returns detailed information about its capabilities, limits, and metadata.")]
    public static async Task<string> ValidateAddress(
        [Description("Lightning Address in email format (e.g., 'alice@wallet.com', 'bob@zaphq.io'). The address to validate and get information about.")] string address)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["LightningAddress"] = address,
            ["RequestId"] = Guid.NewGuid(),
            ["Operation"] = "Validate"
        });

        _logger.LogInformation($"Starting validation for Lightning Address: {address}");

        try
        {
            using var client = new HttpClient();

            // Parse lightning address
            var parts = address.Split('@');
            if (parts.Length != 2)
            {
                _logger.LogError($"Invalid lightning address format: {address}");
                return "❌ Invalid Format: Lightning address must be in format user@domain.com";
            }

            var username = parts[0];
            var domain = parts[1];

            // Basic format validation
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(domain))
            {
                return "❌ Invalid Format: Username and domain cannot be empty";
            }

            if (!domain.Contains('.'))
            {
                return "❌ Invalid Format: Domain must contain at least one dot";
            }

            _logger.LogDebug($"Validating Lightning Address - Username: {username}, Domain: {domain}");

            // Check LNURL Pay endpoint
            var lnurlEndpoint = $"https://{domain}/.well-known/lnurlp/{username}";
            _logger.LogDebug($"Checking LNURL endpoint: {lnurlEndpoint}");

            var lnurlResponse = await client.GetAsync(lnurlEndpoint);

            if (!lnurlResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning($"LNURL endpoint not reachable. Status: {lnurlResponse.StatusCode}, Endpoint: {lnurlEndpoint}");
                return $"❌ Address Not Found: Lightning Address not available (HTTP {lnurlResponse.StatusCode})";
            }

            var lnurlContent = await lnurlResponse.Content.ReadAsStringAsync();
            _logger.LogDebug($"LNURL response received: {lnurlContent}");

            var lnurlData = JsonSerializer.Deserialize<LnurlPayResponse>(lnurlContent);

            if (lnurlData == null)
            {
                return "❌ Invalid Response: Could not parse LNURL Pay response";
            }

            if (lnurlData.Tag != "payRequest")
            {
                return $"❌ Invalid Protocol: Expected 'payRequest', got '{lnurlData.Tag}'";
            }

            // Build validation result
            var result = new System.Text.StringBuilder();
            result.AppendLine("✅ Lightning Address Valid!");
            result.AppendLine();
            result.AppendLine($"📧 Address: {address}");
            result.AppendLine($"🌐 Domain: {domain}");
            result.AppendLine($"👤 Username: {username}");
            result.AppendLine();

            // Payment limits
            var minSats = lnurlData.MinSendable / 1000;
            var maxSats = lnurlData.MaxSendable / 1000;
            result.AppendLine("💰 Payment Limits:");
            result.AppendLine($"   • Minimum: {minSats:N0} sats");
            result.AppendLine($"   • Maximum: {maxSats:N0} sats");
            result.AppendLine();

            // Parse metadata for additional info
            if (!string.IsNullOrEmpty(lnurlData.Metadata))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<string[][]>(lnurlData.Metadata);
                    if (metadata != null && metadata.Length > 0)
                    {
                        result.AppendLine("ℹ️ Metadata:");
                        foreach (var item in metadata)
                        {
                            if (item.Length >= 2)
                            {
                                var type = item[0];
                                var value = item[1];

                                switch (type)
                                {
                                    case "text/plain":
                                        result.AppendLine($"   • Description: {value}");
                                        break;
                                    case "text/identifier":
                                        result.AppendLine($"   • Identifier: {value}");
                                        break;
                                    case "text/email":
                                        result.AppendLine($"   • Email: {value}");
                                        break;
                                }
                            }
                        }
                        result.AppendLine();
                    }
                }
                catch (Exception metaEx)
                {
                    _logger.LogWarning($"Could not parse metadata: {metaEx.Message}");
                }
            }

            // Additional features
            result.AppendLine("🔧 Features:");
            result.AppendLine("   • LNURL Pay: ✅ Supported");
            
            if (lnurlData.CommentAllowed > 0)
            {
                result.AppendLine($"   • Comments: ✅ Up to {lnurlData.CommentAllowed} characters");
            }
            else
            {
                result.AppendLine("   • Comments: ❌ Not supported");
            }

            // Callback URL validation
            if (!string.IsNullOrEmpty(lnurlData.Callback))
            {
                result.AppendLine($"   • Callback URL: ✅ {lnurlData.Callback}");
            }

            _logger.LogInformation($"Successfully validated Lightning Address: {address}");
            return result.ToString();
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, $"Network error while validating {address}");
            return $"❌ Network Error: Could not reach Lightning Address service ({httpEx.Message})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception occurred while validating {address}");
            return $"❌ Validation Error: {ex.Message}";
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
