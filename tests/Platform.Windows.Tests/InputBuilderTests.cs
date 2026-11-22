using System.Runtime.InteropServices;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows;
using EasyMouse.Platform.Windows.Native;

namespace EasyMouse.Platform.Windows.Tests;

public class InputBuilderTests
{
    [Fact]
    public void InputStructMatchesWin32Layout()
    {
        // 40 bytes on x64 (type + padding + MOUSEINPUT), 28 on x86. SendInput rejects any other cbSize.
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, Marshal.SizeOf<INPUT>());
    }

    [Fact]
    public void MouseMoveIsRelative()
    {
        var i = InputBuilder.MouseMove(-7, 12);
        Assert.Equal(InputConst.INPUT_MOUSE, i.type);
        Assert.Equal(InputConst.MOUSEEVENTF_MOVE, i.u.mi.dwFlags);
        Assert.Equal(-7, i.u.mi.dx);
        Assert.Equal(12, i.u.mi.dy);
    }

    [Theory]
    [InlineData(MouseButton.Left, true, 0x0002u)]
    [InlineData(MouseButton.Left, false, 0x0004u)]
    [InlineData(MouseButton.Right, true, 0x0008u)]
    [InlineData(MouseButton.Right, false, 0x0010u)]
    [InlineData(MouseButton.Middle, true, 0x0020u)]
    [InlineData(MouseButton.Middle, false, 0x0040u)]
    public void ButtonFlags(MouseButton b, bool down, uint expected)
        => Assert.Equal(expected, InputBuilder.MouseButton(b, down).u.mi.dwFlags);

    [Fact]
    public void WheelVerticalAndHorizontalAreSeparateEvents()
    {
        var buf = new INPUT[2];
        var n = InputBuilder.Wheel(-120, 240, buf);
        Assert.Equal(2, n);
        Assert.Equal(InputConst.MOUSEEVENTF_WHEEL, buf[0].u.mi.dwFlags);
        Assert.Equal(240, unchecked((int)buf[0].u.mi.mouseData));
        Assert.Equal(InputConst.MOUSEEVENTF_HWHEEL, buf[1].u.mi.dwFlags);
        Assert.Equal(-120, unchecked((int)buf[1].u.mi.mouseData)); // signed value stored in a DWORD
    }

    [Fact]
    public void WheelZeroAxisProducesNoEvent()
    {
        var buf = new INPUT[2];
        Assert.Equal(0, InputBuilder.Wheel(0, 0, buf));
        Assert.Equal(1, InputBuilder.Wheel(0, -5, buf));
        Assert.Equal(InputConst.MOUSEEVENTF_WHEEL, buf[0].u.mi.dwFlags);
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(-3, -3)]
    [InlineData(int.MaxValue, InputBuilder.MaxWheel)]
    [InlineData(int.MinValue, -InputBuilder.MaxWheel)]
    public void WheelDeltaIsClamped(int input, int expected) => Assert.Equal(expected, InputBuilder.ClampWheel(input));

    [Theory]
    [InlineData((ushort)0x25, true)]  // Left
    [InlineData((ushort)0x2E, true)]  // Delete
    [InlineData((ushort)0x24, true)]  // Home
    [InlineData((ushort)0xA3, true)]  // RControl
    [InlineData((ushort)0x10, false)] // Shift
    [InlineData((ushort)0x0D, false)] // Enter
    [InlineData((ushort)0x41, false)] // A
    public void ExtendedKeys(ushort vk, bool extended) => Assert.Equal(extended, InputBuilder.IsExtendedKey(vk));

    [Fact]
    public void KeyUpCarriesKeyUpFlagAndExtendedFlag()
    {
        var down = InputBuilder.Key(0x26, true);
        var up = InputBuilder.Key(0x26, false);
        Assert.Equal(InputConst.KEYEVENTF_EXTENDEDKEY, down.u.ki.dwFlags);
        Assert.Equal(InputConst.KEYEVENTF_EXTENDEDKEY | InputConst.KEYEVENTF_KEYUP, up.u.ki.dwFlags);
        Assert.Equal(0x26, down.u.ki.wVk);
    }

    private static List<(ushort vk, ushort scan, uint flags)> Decode(string text)
        => InputBuilder.Text(text).Select(i => (i.u.ki.wVk, i.u.ki.wScan, i.u.ki.dwFlags)).ToList();

    [Fact]
    public void AsciiTextIsUnicodeDownUpPairs()
    {
        var e = Decode("hi");
        Assert.Equal(4, e.Count);
        Assert.All(e, x => Assert.Equal(0, x.vk));
        Assert.Equal(new ushort[] { 'h', 'h', 'i', 'i' }, e.Select(x => x.scan));
        Assert.Equal(InputConst.KEYEVENTF_UNICODE, e[0].flags);
        Assert.Equal(InputConst.KEYEVENTF_UNICODE | InputConst.KEYEVENTF_KEYUP, e[1].flags);
    }

    [Fact]
    public void HebrewIsLayoutIndependentUnicode()
    {
        var e = Decode("שלום");
        Assert.Equal(8, e.Count);
        Assert.Equal((ushort)'ש', e[0].scan);
        Assert.All(e, x => Assert.True((x.flags & InputConst.KEYEVENTF_UNICODE) != 0));
    }

    [Fact]
    public void SurrogatePairIsFourEventsHighThenLow()
    {
        var e = Decode("😀"); // U+1F600 = D83D DE00
        Assert.Equal(new ushort[] { 0xD83D, 0xD83D, 0xDE00, 0xDE00 }, e.Select(x => x.scan));
    }

    [Fact]
    public void UnpairedSurrogatesAreDropped()
    {
        Assert.Empty(Decode("\uD83D"));
        Assert.Empty(Decode("\uDE00"));
        Assert.Equal(2, Decode("\uD83Da").Count); // only 'a' survives
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void NewlinesBecomeOneEnter(string s)
    {
        var e = Decode(s);
        Assert.Equal(2, e.Count);
        Assert.All(e, x => Assert.Equal(InputConst.VK_RETURN, x.vk));
    }

    [Fact]
    public void TabIsTabKeyAndOtherControlsAreDropped()
    {
        var e = Decode("a\tb\u0001");
        Assert.Equal(6, e.Count);
        Assert.Equal(InputConst.VK_TAB, e[2].vk);
    }

    [Fact]
    public void EmptyTextProducesNothing() => Assert.Empty(InputBuilder.Text(""));
}
