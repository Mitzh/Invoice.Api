namespace Mitzh.Invoice.Api;

public sealed record ApiResponse<T>(int Code, string Msg, T Data);
internal static class ApiResult
{
    public static IResult Ok<T>(T data) => Results.Ok(new ApiResponse<T>(0, null, data));
    public static IResult Error(string message, int statusCode) => Results.Json(new ApiResponse<object>(1, message, null), statusCode: statusCode);
    public static IResult NotFound() => Error("Resource not found.", 404);
    public static IResult Accepted<T>(string location, T data) => Results.Accepted(location, new ApiResponse<T>(0, null, data));
}
