using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Security;

/// <summary>
/// Hand-written checks that run after deserialization (decisions §9): patterns, lengths, bounds and the token format.
/// The public constants mirror /schemas; a test fails when they drift.
/// </summary>
public static class ContractValidator
{
    // Patterns are anchored with \A…\z: in .NET "$" also matches before a trailing "\n".
    public const string IdPattern = @"\A[a-z0-9][a-z0-9-]{0,63}\z";
    public const int IdMaxLength = 64;
    public const string TokenPattern = @"\A[A-Za-z0-9_-]{43}\z";
    public const int TokenLength = 43;
    public const int TokenBytes = 32;
    public const string CodePattern = @"\A[0-9]{6}\z";
    public const int CodeLength = 6;
    public const string DeviceNamePattern = @"\A[^\u0000-\u001f\u007f]+\z";
    public const int DeviceNameMinLength = 1;
    public const int DeviceNameMaxLength = 64;
    public const int TemplateNameMinLength = 1;
    public const int TemplateNameMaxLength = 64;
    public const int TemplateSchemaVersion = 1;
    public const int PositionMin = 0;
    public const int PositionMax = 7;
    public const int ScreenSizeMin = 200;
    public const int ScreenSizeMax = 4096;
    public const double SafeAreaMin = 0;
    public const double SafeAreaMax = 512;
    public const double PixelRatioMin = 1;
    public const double PixelRatioMax = 4;

    /// <summary>Wire names of <see cref="Gesture"/> as in the schema.</summary>
    public static readonly IReadOnlyList<string> GestureNames = ["tap", "hold", "minus", "plus", "previous", "next"];

    /// <summary>Wire names of <see cref="Cutout"/> as in the schema.</summary>
    public static readonly IReadOnlyList<string> CutoutNames = ["none", "notch", "dynamicIsland", "unknown"];

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private static readonly Regex IdRegex = new(IdPattern, Options);
    private static readonly Regex TokenRegex = new(TokenPattern, Options);
    private static readonly Regex CodeRegex = new(CodePattern, Options);
    private static readonly Regex DeviceNameRegex = new(DeviceNamePattern, Options);

    public static bool IsValid(ClientMessage? message) => message switch
    {
        HelloMessage hello => IsValidToken(hello.Token),
        GestureMessage gesture => IsValidId(gesture.WidgetId) && Enum.IsDefined(gesture.Gesture),
        SelectMessage select => IsValidId(select.WidgetId) && select.Position is >= PositionMin and <= PositionMax,
        _ => false,
    };

    public static bool IsValid(PairRequest? request) =>
        request is not null && IsValidCode(request.Code) && IsValidScreen(request.Screen);

    public static bool IsValid(RenameDeviceRequest? request) => request is not null && IsValidDeviceName(request.Name);

    public static bool IsValid(StartPairingRequest? request) =>
        request is not null && (request.TemplateId is null || IsValidId(request.TemplateId));

    public static bool IsValid(DeviceTemplate? template) =>
        template is not null
        && template.SchemaVersion == TemplateSchemaVersion
        && IsValidId(template.Id)
        && HasLengthInCodePoints(template.Name, TemplateNameMinLength, TemplateNameMaxLength)
        && IsValidScreen(template.Screen)
        && Enum.IsDefined(template.Cutout);

    public static bool IsValidId(string? value) =>
        value is { Length: <= IdMaxLength } && IdRegex.IsMatch(value);

    /// <summary>Base64url alphabet, exactly 43 characters, decoding to exactly 32 bytes.</summary>
    public static bool IsValidToken(string? value) => TryDecodeToken(value, out _);

    public static bool IsValidCode(string? value) =>
        value is { Length: CodeLength } && CodeRegex.IsMatch(value);

    /// <summary>1..64 Unicode code points, no U+0000–U+001F or U+007F.</summary>
    public static bool IsValidDeviceName(string? value) =>
        value is not null
        && HasLengthInCodePoints(value, DeviceNameMinLength, DeviceNameMaxLength)
        && DeviceNameRegex.IsMatch(value);

    public static bool IsValidScreen(ScreenInfo? screen)
    {
        if (screen is null || screen.SafeArea is null)
        {
            return false;
        }

        var area = screen.SafeArea;
        // Comparisons with NaN are false, so NaN and infinities fail every range check.
        return screen.Width is >= ScreenSizeMin and <= ScreenSizeMax
            && screen.Height is >= ScreenSizeMin and <= ScreenSizeMax
            && InRange(area.Top, SafeAreaMin, SafeAreaMax)
            && InRange(area.Right, SafeAreaMin, SafeAreaMax)
            && InRange(area.Bottom, SafeAreaMin, SafeAreaMax)
            && InRange(area.Left, SafeAreaMin, SafeAreaMax)
            && InRange(screen.PixelRatio, PixelRatioMin, PixelRatioMax)
            && area.Left + area.Right < screen.Width
            && area.Top + area.Bottom < screen.Height;
    }

    internal static bool TryDecodeToken(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is not { Length: TokenLength } || !TokenRegex.IsMatch(value))
        {
            return false;
        }

        var buffer = new byte[TokenBytes + 2];
        int written;
        try
        {
            written = Base64Url.DecodeFromChars(value, buffer);
        }
        catch (FormatException)
        {
            // Raised for non-canonical trailing bits as well.
            return false;
        }

        if (written != TokenBytes)
        {
            return false;
        }

        var decoded = buffer.AsSpan(0, TokenBytes).ToArray();
        // Canonical form only: the last character carries 2 spare bits that must be zero, so one token has one spelling.
        if (!string.Equals(Base64Url.EncodeToString(decoded), value, StringComparison.Ordinal))
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    private static bool InRange(double value, double min, double max) => value >= min && value <= max;

    // Counts code points and rejects lone surrogates (they cannot be encoded as UTF-8 text).
    internal static bool HasLengthInCodePoints(string? value, int min, int max)
    {
        if (value is null)
        {
            return false;
        }

        var count = 0;
        var i = 0;
        while (i < value.Length)
        {
            if (!Rune.TryGetRuneAt(value, i, out var rune))
            {
                return false;
            }

            i += rune.Utf16SequenceLength;
            if (++count > max)
            {
                return false;
            }
        }

        return count >= min;
    }
}
