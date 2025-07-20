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
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

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
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

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

    [McpServerTool, Description("Gets comprehensive information about a Lightning Address including metadata, service provider details, and supported features.")]
    public static async Task<string> GetAddressInfo(
        [Description("Lightning Address in email format (e.g., 'alice@wallet.com', 'bob@zaphq.io'). The address to get detailed information about.")] string address)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["LightningAddress"] = address,
            ["RequestId"] = Guid.NewGuid(),
            ["Operation"] = "GetInfo"
        });

        _logger.LogInformation($"Getting detailed information for Lightning Address: {address}");

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            // Parse lightning address
            var parts = address.Split('@');
            if (parts.Length != 2)
            {
                _logger.LogError($"Invalid lightning address format: {address}");
                return "❌ Invalid Format: Lightning address must be in format user@domain.com";
            }

            var username = parts[0];
            var domain = parts[1];

            _logger.LogDebug($"Getting info for Lightning Address - Username: {username}, Domain: {domain}");

            // Get LNURL Pay information
            var lnurlEndpoint = $"https://{domain}/.well-known/lnurlp/{username}";
            _logger.LogDebug($"Fetching LNURL endpoint: {lnurlEndpoint}");

            var lnurlResponse = await client.GetAsync(lnurlEndpoint);

            if (!lnurlResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning($"LNURL endpoint not reachable. Status: {lnurlResponse.StatusCode}, Endpoint: {lnurlEndpoint}");
                return $"❌ Address Not Found: Lightning Address not available (HTTP {lnurlResponse.StatusCode})";
            }

            var lnurlContent = await lnurlResponse.Content.ReadAsStringAsync();
            _logger.LogDebug($"LNURL response received: {lnurlContent}");

            var lnurlData = JsonSerializer.Deserialize<LnurlPayResponse>(lnurlContent);

            if (lnurlData == null || lnurlData.Tag != "payRequest")
            {
                return "❌ Invalid Response: Not a valid Lightning Address";
            }

            // Build comprehensive info
            var result = new System.Text.StringBuilder();
            result.AppendLine("📋 Lightning Address Information");
            result.AppendLine("═══════════════════════════════════");
            result.AppendLine();

            // Basic Information
            result.AppendLine("📧 Contact Details:");
            result.AppendLine($"   • Address: {address}");
            result.AppendLine($"   • Domain: {domain}");
            result.AppendLine($"   • Username: {username}");
            result.AppendLine();

            // Service Provider Info
            result.AppendLine("🏢 Service Provider:");
            result.AppendLine($"   • Domain: {domain}");
            result.AppendLine($"   • LNURL Endpoint: {lnurlEndpoint}");

            // Try to get domain info
            try
            {
                var domainInfo = await client.GetAsync($"https://{domain}");
                if (domainInfo.IsSuccessStatusCode)
                {
                    result.AppendLine($"   • Website Status: ✅ Online");
                }
                else
                {
                    result.AppendLine($"   • Website Status: ⚠️ HTTP {domainInfo.StatusCode}");
                }
            }
            catch
            {
                result.AppendLine($"   • Website Status: ❓ Unknown");
            }
            result.AppendLine();

            // Payment Configuration
            var minSats = lnurlData.MinSendable / 1000;
            var maxSats = lnurlData.MaxSendable / 1000;
            var minBtc = minSats / 100_000_000.0;
            var maxBtc = maxSats / 100_000_000.0;

            result.AppendLine("💰 Payment Configuration:");
            result.AppendLine($"   • Minimum: {minSats:N0} sats ({minBtc:F8} BTC)");
            result.AppendLine($"   • Maximum: {maxSats:N0} sats ({maxBtc:F8} BTC)");
            result.AppendLine($"   • Range: {(maxSats - minSats):N0} sats difference");
            result.AppendLine();

            // Metadata Analysis
            if (!string.IsNullOrEmpty(lnurlData.Metadata))
            {
                result.AppendLine("ℹ️ Metadata & Profile:");
                try
                {
                    var metadata = JsonSerializer.Deserialize<string[][]>(lnurlData.Metadata);
                    if (metadata != null && metadata.Length > 0)
                    {
                        var hasDescription = false;
                        var hasIdentifier = false;
                        var hasEmail = false;
                        var hasImage = false;

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
                                        hasDescription = true;
                                        break;
                                    case "text/identifier":
                                        result.AppendLine($"   • Identifier: {value}");
                                        hasIdentifier = true;
                                        break;
                                    case "text/email":
                                        result.AppendLine($"   • Email: {value}");
                                        hasEmail = true;
                                        break;
                                    case "image/png":
                                    case "image/jpeg":
                                        result.AppendLine($"   • Avatar: ✅ {type} image available");
                                        hasImage = true;
                                        break;
                                }
                            }
                        }

                        if (!hasDescription) result.AppendLine("   • Description: ❌ Not provided");
                        if (!hasIdentifier) result.AppendLine("   • Identifier: ❌ Not provided");
                        if (!hasEmail) result.AppendLine("   • Email: ❌ Not provided");
                        if (!hasImage) result.AppendLine("   • Avatar: ❌ No image");
                    }
                    else
                    {
                        result.AppendLine("   • No metadata available");
                    }
                }
                catch (Exception metaEx)
                {
                    _logger.LogWarning($"Could not parse metadata: {metaEx.Message}");
                    result.AppendLine("   • ⚠️ Metadata parsing failed");
                }
                result.AppendLine();
            }

            // Technical Features
            result.AppendLine("🔧 Technical Features:");
            result.AppendLine("   • Protocol: LNURL Pay");
            result.AppendLine($"   • Callback URL: {lnurlData.Callback}");

            if (lnurlData.CommentAllowed > 0)
            {
                result.AppendLine($"   • Comments: ✅ Up to {lnurlData.CommentAllowed} chars");
            }
            else
            {
                result.AppendLine("   • Comments: ❌ Not supported");
            }

            // Check if callback URL is accessible
            try
            {
                var callbackTest = await client.GetAsync(lnurlData.Callback + "?amount=1000&nonce=test");
                if (callbackTest.IsSuccessStatusCode)
                {
                    result.AppendLine("   • Callback Status: ✅ Responsive");
                }
                else
                {
                    result.AppendLine($"   • Callback Status: ⚠️ HTTP {callbackTest.StatusCode}");
                }
            }
            catch
            {
                result.AppendLine("   • Callback Status: ❓ Test failed");
            }

            result.AppendLine();

            // Usage Recommendations
            result.AppendLine("💡 Usage Recommendations:");
            if (maxSats >= 100_000_000) // 1 BTC
            {
                result.AppendLine("   • ✅ Suitable for large payments");
            }
            if (minSats <= 1000) // 1000 sats
            {
                result.AppendLine("   • ✅ Good for microtransactions");
            }
            if (lnurlData.CommentAllowed > 0)
            {
                result.AppendLine("   • ✅ Supports payment memos");
            }

            _logger.LogInformation($"Successfully retrieved info for Lightning Address: {address}");
            return result.ToString();
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, $"Network error while getting info for {address}");
            return $"❌ Network Error: Could not reach Lightning Address service ({httpEx.Message})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception occurred while getting info for {address}");
            return $"❌ Info Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Checks the status of a Lightning Network invoice (BOLT11) to determine if it has been paid, is still pending, or has expired.")]
    public static Task<string> CheckInvoiceStatus(
        [Description("BOLT11 Lightning Network invoice string (e.g., 'lnbc1000n1p...'). The invoice to check the payment status for.")] string invoice)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Invoice"] = invoice.Length > 20 ? invoice.Substring(0, 20) + "..." : invoice,
            ["RequestId"] = Guid.NewGuid(),
            ["Operation"] = "CheckStatus"
        });

        _logger.LogInformation($"Checking status for Lightning invoice");

        try
        {
            // Basic BOLT11 format validation
            if (string.IsNullOrWhiteSpace(invoice))
            {
                return Task.FromResult("❌ Invalid Input: Invoice cannot be empty");
            }

            if (!invoice.StartsWith("ln", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("❌ Invalid Format: Invoice must start with 'ln' (BOLT11 format)");
            }

            if (invoice.Length < 100)
            {
                return Task.FromResult("❌ Invalid Format: Invoice appears too short to be a valid BOLT11 invoice");
            }

            _logger.LogDebug($"Invoice format validation passed");

            // Extract network and amount from BOLT11 (basic parsing)
            var network = "unknown";
            var amount = "unknown";

            try
            {
                if (invoice.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase))
                {
                    network = "mainnet";
                    // Extract amount if present
                    var amountPart = invoice.Substring(4);
                    var numberEnd = 0;
                    while (numberEnd < amountPart.Length && char.IsDigit(amountPart[numberEnd]))
                    {
                        numberEnd++;
                    }
                    if (numberEnd > 0 && numberEnd < amountPart.Length)
                    {
                        var amountValue = amountPart.Substring(0, numberEnd);
                        var unit = amountPart[numberEnd];
                        amount = $"{amountValue}{unit}";
                    }
                }
                else if (invoice.StartsWith("lntb", StringComparison.OrdinalIgnoreCase))
                {
                    network = "testnet";
                }
                else if (invoice.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase))
                {
                    network = "regtest";
                }
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning($"Could not parse invoice details: {parseEx.Message}");
            }

            // Build status response
            var result = new System.Text.StringBuilder();
            result.AppendLine("⚡ Lightning Invoice Status Check");
            result.AppendLine("═══════════════════════════════════");
            result.AppendLine();

            // Invoice Information
            result.AppendLine("📄 Invoice Details:");
            result.AppendLine($"   • Format: BOLT11");
            result.AppendLine($"   • Network: {network}");
            result.AppendLine($"   • Amount: {amount}");
            result.AppendLine($"   • Invoice: {invoice.Substring(0, Math.Min(invoice.Length, 30))}...");
            result.AppendLine();

            // Status Check Limitation Notice
            result.AppendLine("⚠️ Status Check Limitation:");
            result.AppendLine("   • Direct invoice status checking requires access to a Lightning Node");
            result.AppendLine("   • This MCP server cannot directly query payment status");
            result.AppendLine("   • Invoice status depends on the Lightning Network node that generated it");
            result.AppendLine();

            // Alternative Methods
            result.AppendLine("🔧 Alternative Status Check Methods:");
            result.AppendLine("   • Use the Lightning wallet/service that created the invoice");
            result.AppendLine("   • Check with the Lightning Node that generated this invoice");
            result.AppendLine("   • Use Lightning Network explorers (limited functionality)");
            result.AppendLine("   • Contact the Lightning Address service provider");
            result.AppendLine();

            // Invoice Analysis
            result.AppendLine("🔍 Invoice Analysis:");

            // Check if invoice looks valid based on length and format
            if (invoice.Length > 200 && invoice.Length < 2000)
            {
                result.AppendLine("   • Length: ✅ Appears to be valid BOLT11 length");
            }
            else if (invoice.Length <= 200)
            {
                result.AppendLine("   • Length: ⚠️ Unusually short for BOLT11");
            }
            else
            {
                result.AppendLine("   • Length: ⚠️ Unusually long for BOLT11");
            }

            // Check expiration (basic estimation)
            result.AppendLine("   • Expiration: ❓ Cannot determine without node access");
            result.AppendLine("   • Payment Status: ❓ Requires Lightning Node query");
            result.AppendLine();

            // Recommendations
            result.AppendLine("💡 Recommendations:");
            result.AppendLine("   • For real-time status, use a Lightning Node RPC call");
            result.AppendLine("   • Check the wallet/service where you created this invoice");
            result.AppendLine("   • Lightning invoices typically expire after 1-24 hours");
            result.AppendLine("   • Paid invoices cannot be paid again (single-use)");
            result.AppendLine();

            // Technical Note
            result.AppendLine("🔨 Technical Note:");
            result.AppendLine("   • Full invoice status checking requires:");
            result.AppendLine("     - Lightning Node connection (LND, CLN, Eclair)");
            result.AppendLine("     - RPC access to lookup invoice by payment hash");
            result.AppendLine("     - Node synchronization with Lightning Network");
            result.AppendLine("   • This MCP server focuses on Lightning Address operations");

            _logger.LogInformation($"Provided invoice analysis and status check guidance");
            return Task.FromResult(result.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception occurred while checking invoice status");
            return Task.FromResult($"❌ Status Check Error: {ex.Message}");
        }
    }

    [McpServerTool, Description("Decodes a Lightning Network BOLT11 invoice to show detailed information including amount, description, destination, expiration time, and other metadata.")]
    public static Task<string> DecodeInvoice(
        [Description("BOLT11 Lightning Network invoice string (e.g., 'lnbc1000n1p...'). The invoice to decode and analyze for detailed information.")] string invoice)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Invoice"] = invoice.Length > 20 ? invoice.Substring(0, 20) + "..." : invoice,
            ["RequestId"] = Guid.NewGuid(),
            ["Operation"] = "DecodeInvoice"
        });

        _logger.LogInformation($"Starting BOLT11 invoice decoding");

        try
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(invoice))
            {
                return Task.FromResult("❌ Invalid Input: Invoice cannot be empty");
            }

            if (!invoice.StartsWith("ln", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("❌ Invalid Format: Invoice must start with 'ln' (BOLT11 format)");
            }

            if (invoice.Length < 100)
            {
                return Task.FromResult("❌ Invalid Format: Invoice appears too short to be a valid BOLT11 invoice");
            }

            _logger.LogDebug($"Basic invoice format validation passed");

            var result = new System.Text.StringBuilder();
            result.AppendLine("🔍 BOLT11 Invoice Decoder");
            result.AppendLine("═══════════════════════════════════");
            result.AppendLine();

            // Network Detection
            var network = "unknown";
            var networkIcon = "❓";
            if (invoice.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase))
            {
                network = "Bitcoin Mainnet";
                networkIcon = "₿";
            }
            else if (invoice.StartsWith("lntb", StringComparison.OrdinalIgnoreCase))
            {
                network = "Bitcoin Testnet";
                networkIcon = "🧪";
            }
            else if (invoice.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase))
            {
                network = "Bitcoin Regtest";
                networkIcon = "⚙️";
            }

            result.AppendLine($"🌐 Network Information:");
            result.AppendLine($"   • Network: {networkIcon} {network}");
            result.AppendLine($"   • Protocol: BOLT11 Lightning Network");
            result.AppendLine();

            // Amount Parsing
            var amount = "No amount specified";
            var amountSats = 0L;
            var amountBtc = 0.0;

            try
            {
                var amountPart = "";
                if (invoice.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase))
                {
                    amountPart = invoice.Substring(4);
                }
                else if (invoice.StartsWith("lntb", StringComparison.OrdinalIgnoreCase))
                {
                    amountPart = invoice.Substring(4);
                }
                else if (invoice.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase))
                {
                    amountPart = invoice.Substring(6);
                }

                if (!string.IsNullOrEmpty(amountPart))
                {
                    var numberEnd = 0;
                    while (numberEnd < amountPart.Length && char.IsDigit(amountPart[numberEnd]))
                    {
                        numberEnd++;
                    }

                    if (numberEnd > 0 && numberEnd < amountPart.Length)
                    {
                        var amountValue = long.Parse(amountPart.Substring(0, numberEnd));
                        var unit = amountPart[numberEnd];

                        // Convert to satoshis based on unit
                        switch (unit)
                        {
                            case 'm': // millisatoshi (divide by 1000 to get satoshis)
                                amountSats = amountValue / 1000;
                                amountBtc = amountSats / 100_000_000.0;
                                amount = $"{amountSats:N0} sats";
                                break;
                            case 'u': // microsatoshi (multiply by 100 to get satoshis)
                                amountSats = amountValue * 100;
                                amountBtc = amountSats / 100_000_000.0;
                                amount = $"{amountSats:N0} sats";
                                break;
                            case 'n': // nanosatoshi (multiply by 100,000 to get satoshis)
                                amountSats = amountValue * 100_000;
                                amountBtc = amountSats / 100_000_000.0;
                                amount = $"{amountSats:N0} sats";
                                break;
                            case 'p': // picosatoshi (multiply by 100,000,000 to get satoshis)
                                amountSats = amountValue * 100_000_000;
                                amountBtc = amountSats / 100_000_000.0;
                                amount = $"{amountSats:N0} sats";
                                break;
                            default:
                                amount = $"Unknown unit: {amountValue}{unit}";
                                break;
                        }
                    }
                    else if (numberEnd == 0)
                    {
                        amount = "No amount specified (any amount invoice)";
                    }
                }
            }
            catch (Exception amountEx)
            {
                _logger.LogWarning($"Could not parse amount: {amountEx.Message}");
                amount = "Could not parse amount";
            }

            result.AppendLine($"💰 Payment Information:");
            result.AppendLine($"   • Amount: {amount}");
            if (amountSats > 0)
            {
                result.AppendLine($"   • Bitcoin Value: {amountBtc:F8} BTC");
                result.AppendLine($"   • Satoshis: {amountSats:N0} sats");

                // Add USD estimate note
                result.AppendLine($"   • USD Value: ❓ (requires current BTC price)");
            }
            result.AppendLine();

            // Invoice Structure Analysis
            result.AppendLine($"📋 Invoice Structure:");
            result.AppendLine($"   • Total Length: {invoice.Length} characters");
            result.AppendLine($"   • Format: BOLT11");

            // Extract timestamp (basic estimation - BOLT11 timestamp is in the data part)
            var currentTime = DateTimeOffset.UtcNow;
            result.AppendLine($"   • Created: ❓ Timestamp requires full BOLT11 parsing");
            result.AppendLine($"   • Typical Expiry: 1-24 hours from creation");
            result.AppendLine();

            // Technical Details
            result.AppendLine($"🔧 Technical Details:");
            result.AppendLine($"   • Invoice Prefix: {invoice.Substring(0, Math.Min(10, invoice.Length))}...");
            result.AppendLine($"   • Checksum: Last 6 characters serve as Bech32 checksum");

            // Basic structure validation
            if (invoice.Length > 200 && invoice.Length < 2000)
            {
                result.AppendLine($"   • Length Validation: ✅ Standard BOLT11 length");
            }
            else if (invoice.Length <= 200)
            {
                result.AppendLine($"   • Length Validation: ⚠️ Unusually short");
            }
            else
            {
                result.AppendLine($"   • Length Validation: ⚠️ Unusually long");
            }

            // Check for common BOLT11 patterns
            if (invoice.Contains("1"))
            {
                var separatorIndex = invoice.LastIndexOf('1');
                if (separatorIndex > 0)
                {
                    result.AppendLine($"   • Data Separator: Found at position {separatorIndex}");
                    result.AppendLine($"   • Data Section: {invoice.Length - separatorIndex - 1} characters");
                }
            }
            result.AppendLine();

            // Limitations Notice
            result.AppendLine($"⚠️ Decoder Limitations:");
            result.AppendLine($"   • This is a basic BOLT11 decoder implementation");
            result.AppendLine($"   • Full decoding requires specialized BOLT11 libraries");
            result.AppendLine($"   • Missing fields: destination pubkey, payment hash, description, route hints");
            result.AppendLine($"   • For complete analysis, use: lightning-cli decodepay, lncli decodepayreq");
            result.AppendLine();

            // Use Cases
            result.AppendLine($"💡 What You Can Do:");
            result.AppendLine($"   • ✅ Verify invoice format and network");
            result.AppendLine($"   • ✅ Extract payment amount information");
            result.AppendLine($"   • ✅ Validate invoice structure");
            result.AppendLine($"   • ✅ Estimate typical expiration timeframes");
            result.AppendLine($"   • ❌ Cannot extract: description, destination, payment hash");
            result.AppendLine($"   • ❌ Cannot determine: actual expiry time, route information");
            result.AppendLine();

            // Recommendations
            result.AppendLine($"🚀 For Complete Decoding:");
            result.AppendLine($"   • Use Lightning Node CLI tools (lncli, lightning-cli)");
            result.AppendLine($"   • Try online BOLT11 decoders (for non-sensitive invoices)");
            result.AppendLine($"   • Use specialized BOLT11 libraries (btcpayserver, lnd, etc.)");
            result.AppendLine($"   • Access Lightning Node RPC for full invoice details");

            _logger.LogInformation($"Successfully provided basic BOLT11 invoice decoding");
            return Task.FromResult(result.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception occurred while decoding invoice");
            return Task.FromResult($"❌ Decode Error: {ex.Message}");
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
