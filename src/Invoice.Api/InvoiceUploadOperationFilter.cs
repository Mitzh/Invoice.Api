using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Mitzh.Invoice.Api;
// Upload handlers intentionally read HttpRequest themselves to support raw/non-seekable streams.
// Supply explicit OpenAPI bodies without changing that runtime binding contract.
public sealed class InvoiceUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        string path = context.ApiDescription.RelativePath;
        if (path == "api/invoices/recognize/xml" && context.ApiDescription.HttpMethod == "POST")
        {
            operation.RequestBody.Content["application/json"].Example = new OpenApiObject
            {
                ["xml"] = new OpenApiString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "einvoice.xml")))
            };
            return;
        }

        if (context.ApiDescription.HttpMethod != "POST" || path is not ("api/invoices" or "api/invoices/recognize"))
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "object",
                        Required = new HashSet<string>
                        {
                            "file"
                        },
                        Properties = new Dictionary<string, OpenApiSchema>
                        {
                            ["file"] = new()
                            {
                                Type = "string",
                                Format = "binary",
                                Description = "选择一份 XML/OFD/PDF/JPG/PNG 文件（默认最多 20 MiB）。"
                            }
                        }
                    }
                }
            }
        };
        if (path == "api/invoices/recognize")
        {
            operation.RequestBody.Content["application/octet-stream"] = new()
            {
                Schema = new()
                {
                    Type = "string",
                    Format = "binary"
                }
            };
            operation.RequestBody.Content["application/xml"] = new()
            {
                Schema = new()
                {
                    Type = "string"
                },
                Example = new OpenApiString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "einvoice.xml")))
            };
        }
    }
}
