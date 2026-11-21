using Net.Codecrete.QrCodeGenerator;

namespace EasyMouse.App.Logic;

/// <summary>Encodes a URL as a QR module matrix (<c>[x, y]</c>, true = dark). Medium error correction: a phone camera scans a screen easily.</summary>
public static class QrMatrix
{
    public static bool[,] Encode(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var matrix = new bool[qr.Size, qr.Size];
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                matrix[x, y] = qr.GetModule(x, y);
        return matrix;
    }
}
