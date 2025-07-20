# Lightning Address MCP Server

A Model Context Protocol (MCP) server that provides Lightning Network address functionality. This server enables creation of Lightning Network invoices from Lightning Addresses using the LNURL Pay protocol.

## Features

- **Lightning Address Support**: Convert Lightning Addresses (user@domain.com) to BOLT11 invoices
- **LNURL Pay Protocol**: Full implementation of the LNURL Pay specification
- **Comprehensive Logging**: Structured logging with request correlation and detailed debugging
- **Error Handling**: Robust error handling with descriptive error messages
- **MCP Integration**: Seamless integration with Model Context Protocol clients

## Requirements

- .NET 8.0 or later
- Internet connection for Lightning Address resolution

## Installation

1. Clone the repository:
```bash
git clone <repository-url>
cd LnAddressMcp
```

2. Build the project:
```bash
dotnet build
```

3. Run the server:
```bash
dotnet run
```

The server will start on:
- HTTP: `http://localhost:5002`
- HTTPS: `https://localhost:6002`

## Usage

### MCP Tool: CreateInvoice

Creates a Lightning Network invoice from a Lightning Address.

**Parameters:**
- `address` (string): Lightning Address in email format (e.g., 'alice@wallet.com')
- `amount` (int): Payment amount in satoshis

**Returns:**
- BOLT11 invoice string on success
- Error message on failure

**Example:**
```json
{
  "tool": "CreateInvoice",
  "arguments": {
    "address": "user@zaphq.io",
    "amount": 1000
  }
}
```

## How It Works

1. **Parse Lightning Address**: Extracts username and domain from the email-format address
2. **LNURL Resolution**: Queries the `.well-known/lnurlp/<username>` endpoint on the domain
3. **Validate Limits**: Checks if the requested amount is within the recipient's min/max limits
4. **Invoice Generation**: Calls the LNURL Pay callback URL to generate a BOLT11 invoice
5. **Return Invoice**: Returns the Lightning Network invoice for payment

## Configuration

### Logging

The application uses structured JSON logging. Configure logging levels in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "LnAddressTools": "Debug"
    }
  }
}
```

## Development

### Project Structure

- `Program.cs`: Main application entry point and MCP server configuration
- `LnAddressTools`: Static class containing the CreateInvoice MCP tool
- `LnurlPayResponse`: Model for LNURL Pay endpoint responses
- `LnurlPayCallbackResponse`: Model for invoice generation responses

### Building

```bash
dotnet build
```

### Running

```bash
dotnet run
```

## License

[Add your license information here]

## Contributing

[Add contributing guidelines here]