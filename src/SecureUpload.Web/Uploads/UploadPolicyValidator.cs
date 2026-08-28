using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SecureUpload.Core.Files;

namespace SecureUpload.Web.Uploads;

public sealed record ValidatedUploadPolicy(string FileName, string MediaType);

public sealed class UploadPolicyException(
    string code,
    string message,
    int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class UploadPolicyValidator
{
    private readonly FilePolicyOptions _options;

    public UploadPolicyValidator(IOptions<FilePolicyOptions> options)
    {
        _options = options.Value;
        _options.Validate();
    }

    public long MaximumFileSizeBytes => _options.MaximumFileSizeBytes;

    public ValidatedUploadPolicy Validate(string fileName, string? mediaType)
    {
        var safeName = Path.GetFileName(fileName.Replace('\0', '_')).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new UploadPolicyException("invalid-file-name", "Choose a file with a valid name.");
        }

        if (safeName.Length > 255)
        {
            safeName = safeName[^255..];
        }

        var extension = Path.GetExtension(safeName);
        if (!_options.AllowedExtensions.Contains(extension))
        {
            throw new UploadPolicyException(
                "extension-not-allowed",
                "This file type is not allowed.",
                StatusCodes.Status415UnsupportedMediaType);
        }

        var normalizedMediaType = mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedMediaType) ||
            !_options.AllowedMediaTypes.Contains(normalizedMediaType))
        {
            throw new UploadPolicyException(
                "media-type-not-allowed",
                "This file type is not allowed.",
                StatusCodes.Status415UnsupportedMediaType);
        }

        return new(safeName, normalizedMediaType);
    }

    public static bool IsFileSection(ContentDispositionHeaderValue disposition) =>
        disposition.DispositionType.Equals("form-data") &&
        (!string.IsNullOrEmpty(disposition.FileName.Value) ||
         !string.IsNullOrEmpty(disposition.FileNameStar.Value));
}
