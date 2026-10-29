using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Mitzh.Invoice.Api;

public sealed class ApiResponseSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (!context.Type.IsGenericType || context.Type.GetGenericTypeDefinition() != typeof(ApiResponse<>))
        {
            return;
        }

        schema.Required.UnionWith(new[] { "code", "msg", "data" });
        schema.Properties["code"].Minimum = 0;
        schema.Properties["code"].Maximum = 1;
        schema.Properties["msg"].Nullable = true;
        schema.Properties["msg"].Description = "成功为 null；失败为异常信息或校验说明。";
        OpenApiSchema data = schema.Properties["data"];
        if (data.Reference != null)
        {
            schema.Properties["data"] = new OpenApiSchema { Nullable = true, AllOf = new List<OpenApiSchema> { data } };
        }
        else
        {
            data.Nullable = true;
        }
    }
}
