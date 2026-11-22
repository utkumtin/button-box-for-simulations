using Xunit;
using System.Buffers.Text;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;

namespace ButtonBox.Security.Tests;

public class ContractValidatorTests
{
    private static readonly string ValidToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static ScreenInfo Screen(int w = 874, int h = 402, double top = 0, double right = 62, double bottom = 21, double left = 62, double ratio = 3) =>
        new() { Width = w, Height = h, PixelRatio = ratio, SafeArea = new SafeArea { Top = top, Right = right, Bottom = bottom, Left = left } };

    // Why: ids end up in file names and lookups; a trailing newline must not slip past an anchor ("$" allows it in .NET).
    [Theory]
    [InlineData("a", true)]
    [InlineData("0-a", true)]
    [InlineData("button-1", true)]
    [InlineData("-a", false)]
    [InlineData("A", false)]
    [InlineData("a b", false)]
    [InlineData("abc\n", false)]
    [InlineData("\nabc", false)]
    [InlineData("", false)]
    [InlineData("a_b", false)]
    public void Id_pattern(string id, bool valid) => Assert.Equal(valid, ContractValidator.IsValidId(id));

    // Why: the id limit is 64 characters, not 65.
    [Fact]
    public void Id_length_limit()
    {
        Assert.True(ContractValidator.IsValidId(new string('a', 64)));
        Assert.False(ContractValidator.IsValidId(new string('a', 65)));
        Assert.False(ContractValidator.IsValidId(null));
    }

    // Why: the token is the credential; anything but exactly 32 bytes of base64url must be refused before hashing.
    [Fact]
    public void Token_format()
    {
        Assert.True(ContractValidator.IsValidToken(ValidToken));
        Assert.False(ContractValidator.IsValidToken(ValidToken + "A"), "44 chars");
        Assert.False(ContractValidator.IsValidToken(ValidToken[..42]), "42 chars");
        Assert.False(ContractValidator.IsValidToken(ValidToken[..42] + "="), "padded");
        Assert.False(ContractValidator.IsValidToken(ValidToken[..42] + "\n"), "trailing newline");
        Assert.False(ContractValidator.IsValidToken(ValidToken[..42] + "+"), "standard base64 alphabet");
        Assert.False(ContractValidator.IsValidToken(ValidToken[..42] + "/"), "standard base64 alphabet");
        Assert.False(ContractValidator.IsValidToken(null));
        Assert.False(ContractValidator.IsValidToken(string.Empty));
    }

    // Why: 43 chars can encode 258 bits; the last char carries 2 bits that must be zero, or two strings map to one token.
    [Fact]
    public void Token_with_non_canonical_last_character_is_rejected()
    {
        // 'B' has low bits 01 in the last sextet, which a canonical 32-byte encoding never has (it ends in 'A', 'Q', 'g' or 'w').
        Assert.False(ContractValidator.IsValidToken(new string('A', 42) + "B"));
        Assert.True(ContractValidator.IsValidToken(new string('A', 42) + "A"));
    }

    // Why: the hello message is the first thing an unauthenticated peer can send.
    [Fact]
    public void Hello_message()
    {
        Assert.True(ContractValidator.IsValid(new HelloMessage { Token = ValidToken }));
        Assert.False(ContractValidator.IsValid(new HelloMessage { Token = "x" }));
        Assert.False(ContractValidator.IsValid((ClientMessage?)null));
    }

    // Why: gesture and position drive game output; out-of-range values must never reach the simulator.
    [Fact]
    public void Gesture_and_select_messages()
    {
        Assert.True(ContractValidator.IsValid(new GestureMessage { WidgetId = "w1", Gesture = Gesture.Hold }));
        Assert.False(ContractValidator.IsValid(new GestureMessage { WidgetId = "w1", Gesture = (Gesture)99 }));
        Assert.False(ContractValidator.IsValid(new GestureMessage { WidgetId = "W1", Gesture = Gesture.Tap }));
        Assert.True(ContractValidator.IsValid(new SelectMessage { WidgetId = "w1", Position = 0 }));
        Assert.True(ContractValidator.IsValid(new SelectMessage { WidgetId = "w1", Position = 7 }));
        Assert.False(ContractValidator.IsValid(new SelectMessage { WidgetId = "w1", Position = 8 }));
        Assert.False(ContractValidator.IsValid(new SelectMessage { WidgetId = "w1", Position = -1 }));
        Assert.False(ContractValidator.IsValid(new SelectMessage { WidgetId = "w1\n", Position = 1 }));
    }

    // Why: the pairing code is typed by a human but must be exactly six ASCII digits (not Unicode digits, not padded).
    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12345a", false)]
    [InlineData("12345\n", false)]
    [InlineData("١٢٣٤٥٦", false)]
    public void Pair_request_code(string code, bool valid) =>
        Assert.Equal(valid, ContractValidator.IsValid(new PairRequest { Code = code, Screen = Screen() }));

    // Why: the screen data sizes the layout; absurd or NaN values must be refused at the door.
    [Fact]
    public void Screen_bounds()
    {
        Assert.True(ContractValidator.IsValidScreen(Screen()));
        Assert.True(ContractValidator.IsValidScreen(Screen(w: 200, h: 200, left: 0, right: 0, top: 0, bottom: 0, ratio: 1)));
        Assert.True(ContractValidator.IsValidScreen(Screen(w: 4096, h: 4096, ratio: 4)));
        Assert.False(ContractValidator.IsValidScreen(Screen(w: 199)));
        Assert.False(ContractValidator.IsValidScreen(Screen(w: 4097)));
        Assert.False(ContractValidator.IsValidScreen(Screen(h: 199)));
        Assert.False(ContractValidator.IsValidScreen(Screen(h: 4097)));
        Assert.False(ContractValidator.IsValidScreen(Screen(top: -1)));
        Assert.False(ContractValidator.IsValidScreen(Screen(right: 513)));
        Assert.True(ContractValidator.IsValidScreen(Screen(h: 1100, bottom: 512)));
        Assert.False(ContractValidator.IsValidScreen(Screen(left: 512.5)));
        Assert.False(ContractValidator.IsValidScreen(Screen(ratio: 0.99)));
        Assert.False(ContractValidator.IsValidScreen(Screen(ratio: 4.01)));
        Assert.False(ContractValidator.IsValidScreen(Screen(ratio: double.NaN)));
        Assert.False(ContractValidator.IsValidScreen(Screen(top: double.NaN)));
        Assert.False(ContractValidator.IsValidScreen(Screen(right: double.PositiveInfinity)));
        Assert.False(ContractValidator.IsValidScreen(null));
    }

    // Why: insets that eat the whole viewport are impossible and would produce a zero-size layout.
    [Fact]
    public void Safe_area_must_leave_room()
    {
        Assert.False(ContractValidator.IsValidScreen(Screen(w: 300, left: 150, right: 150)));
        Assert.True(ContractValidator.IsValidScreen(Screen(w: 300, left: 150, right: 149)));
        Assert.False(ContractValidator.IsValidScreen(Screen(h: 300, top: 150, bottom: 150)));
    }

    // Why: the name is shown in the UI and stored; control characters would corrupt logs and displays.
    [Fact]
    public void Device_name_rules()
    {
        Assert.True(ContractValidator.IsValidDeviceName("Utku's iPhone"));
        Assert.True(ContractValidator.IsValidDeviceName(new string('a', 64)));
        Assert.False(ContractValidator.IsValidDeviceName(new string('a', 65)));
        Assert.False(ContractValidator.IsValidDeviceName(""));
        Assert.False(ContractValidator.IsValidDeviceName(null));
        Assert.False(ContractValidator.IsValidDeviceName("a\nb"));
        Assert.False(ContractValidator.IsValidDeviceName("abc\n"));
        Assert.False(ContractValidator.IsValidDeviceName("a\0b"));
        Assert.False(ContractValidator.IsValidDeviceName("a\u007fb"));
        Assert.True(ContractValidator.IsValidDeviceName("a\u0080b"));
        Assert.False(ContractValidator.IsValidDeviceName("a\ud800b"), "lone surrogate");
        // 64 emoji are 128 UTF-16 units but 64 code points.
        Assert.True(ContractValidator.IsValidDeviceName(string.Concat(Enumerable.Repeat("\U0001F4F1", 64))));
        Assert.False(ContractValidator.IsValidDeviceName(string.Concat(Enumerable.Repeat("\U0001F4F1", 65))));
        Assert.True(ContractValidator.IsValid(new RenameDeviceRequest { Name = "ok" }));
        Assert.False(ContractValidator.IsValid(new RenameDeviceRequest { Name = "" }));
        Assert.False(ContractValidator.IsValid((RenameDeviceRequest?)null));
    }

    // Why: templateId is optional, but when present it is an id like any other.
    [Fact]
    public void Start_pairing_request()
    {
        Assert.True(ContractValidator.IsValid(new StartPairingRequest()));
        Assert.True(ContractValidator.IsValid(new StartPairingRequest { TemplateId = "iphone-17" }));
        Assert.False(ContractValidator.IsValid(new StartPairingRequest { TemplateId = "iPhone" }));
        Assert.False(ContractValidator.IsValid(new StartPairingRequest { TemplateId = "" }));
    }

    // Why: shipped and imported templates are untrusted input too.
    [Fact]
    public void Device_template()
    {
        DeviceTemplate Make(int version = 1, string id = "t", string name = "T", Cutout cutout = Cutout.Notch, ScreenInfo? screen = null) =>
            new() { SchemaVersion = version, Id = id, Name = name, Screen = screen ?? Screen(), Cutout = cutout, Generic = false };

        Assert.True(ContractValidator.IsValid(Make()));
        Assert.False(ContractValidator.IsValid(Make(version: 2)));
        Assert.False(ContractValidator.IsValid(Make(id: "T")));
        Assert.False(ContractValidator.IsValid(Make(name: "")));
        Assert.False(ContractValidator.IsValid(Make(name: new string('a', 65))));
        Assert.False(ContractValidator.IsValid(Make(cutout: (Cutout)42)));
        Assert.False(ContractValidator.IsValid(Make(screen: Screen(w: 10))));
        Assert.False(ContractValidator.IsValid((DeviceTemplate?)null));
    }

    // Why: the wire names must match the generated enums, otherwise the constants the drift test checks are meaningless.
    [Fact]
    public void Name_arrays_match_the_generated_enums()
    {
        static string[] Wire<T>() where T : struct, Enum => typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()!.Name).ToArray();

        Assert.Equal(Wire<Gesture>(), ContractValidator.GestureNames);
        Assert.Equal(Wire<Cutout>(), ContractValidator.CutoutNames);
    }

    // Why: strict deserialization alone lets "position: 99" through; this proves the chain parse + validate rejects it.
    [Fact]
    public void Deserialized_out_of_range_message_is_rejected()
    {
        var ok = JsonSerializer.Deserialize<ClientMessage>("{\"type\":\"select\",\"widgetId\":\"w\",\"position\":3}", ContractJson.Options);
        var bad = JsonSerializer.Deserialize<ClientMessage>("{\"type\":\"select\",\"widgetId\":\"w\",\"position\":99}", ContractJson.Options);
        Assert.True(ContractValidator.IsValid(ok));
        Assert.False(ContractValidator.IsValid(bad));
    }
}
