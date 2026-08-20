using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

public static class SupWriter
{
    private const int VideoWidth = 1920;
    private const int VideoHeight = 1080;

    public static void Write(string outPath, string style, string fontName, int fontSize, string[] texts, int[] startMs, int[] endMs)
    {
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            for (int i = 0; i < texts.Length; i++)
            {
                byte[,] indexed;
                int w, h;
                Rasterize(texts[i], style, fontName, fontSize, out indexed, out w, out h);

                int x = (VideoWidth - w) / 2;
                int y = VideoHeight - h - 60;

                byte[] rle = Encode(indexed, w, h);

                WriteDisplaySet(fs, startMs[i], (ushort)i, x, y, w, h, rle);
                WriteClear(fs, endMs[i], (ushort)i);
            }
        }
    }

    private static void Rasterize(string text, string style, string fontName, int fontSize, out byte[,] indexed, out int width, out int height)
    {
        using (var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            SizeF size;
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
            {
                size = pg.MeasureString(text, font);
            }

            width = (int)Math.Ceiling(size.Width) + 24;
            height = (int)Math.Ceiling(size.Height) + 24;

            using (var bmp = new Bitmap(width, height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;

                    if (style == "Outline")
                    {
                        using (var path = new GraphicsPath())
                        using (var pen = new Pen(Color.Black, 3.5f))
                        using (var brush = new SolidBrush(Color.White))
                        {
                            pen.LineJoin = LineJoin.Round;
                            path.AddString(text, font.FontFamily, (int)FontStyle.Regular, fontSize, new PointF(12, 12), StringFormat.GenericTypographic);
                            g.DrawPath(pen, path);
                            g.FillPath(brush, path);
                        }
                    }
                    else
                    {
                        using (var brush = new SolidBrush(Color.White))
                        {
                            g.DrawString(text, font, brush, 12, 12);
                        }
                    }
                }

                indexed = new byte[height, width];
                for (int py = 0; py < height; py++)
                {
                    for (int px = 0; px < width; px++)
                    {
                        Color c = bmp.GetPixel(px, py);
                        if (c.A < 24)
                        {
                            indexed[py, px] = 0;
                        }
                        else
                        {
                            int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                            if (lum > 128)
                            {
                                indexed[py, px] = c.A > 160 ? (byte)1 : (byte)2;
                            }
                            else
                            {
                                indexed[py, px] = 3;
                            }
                        }
                    }
                }
            }
        }
    }

    private static byte[] Encode(byte[,] indexed, int width, int height)
    {
        var outBytes = new List<byte>();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                byte colour = indexed[y, x];
                int run = 1;
                while (x + run < width && indexed[y, x + run] == colour && run < 16383)
                {
                    run++;
                }

                EmitRun(outBytes, colour, run);
                x += run;
            }

            outBytes.Add(0x00);
            outBytes.Add(0x00);
        }

        return outBytes.ToArray();
    }

    private static void EmitRun(List<byte> o, byte colour, int run)
    {
        if (colour == 0)
        {
            while (run > 0)
            {
                int n = Math.Min(run, 16383);
                if (n < 64)
                {
                    o.Add(0x00);
                    o.Add((byte)n);
                }
                else
                {
                    o.Add(0x00);
                    o.Add((byte)(0x40 | (n >> 8)));
                    o.Add((byte)(n & 0xFF));
                }

                run -= n;
            }

            return;
        }

        while (run > 0)
        {
            int n = Math.Min(run, 16383);
            if (n <= 2)
            {
                for (int i = 0; i < n; i++)
                {
                    o.Add(colour);
                }
            }
            else if (n < 64)
            {
                o.Add(0x00);
                o.Add((byte)(0x80 | n));
                o.Add(colour);
            }
            else
            {
                o.Add(0x00);
                o.Add((byte)(0xC0 | (n >> 8)));
                o.Add((byte)(n & 0xFF));
                o.Add(colour);
            }

            run -= n;
        }
    }

    private static void WriteDisplaySet(Stream s, int ms, ushort compNumber, int x, int y, int w, int h, byte[] rle)
    {
        uint pts = (uint)((long)ms * 90);

        var pcs = new List<byte>();
        AddU16(pcs, VideoWidth);
        AddU16(pcs, VideoHeight);
        pcs.Add(0x10);
        AddU16(pcs, compNumber);
        pcs.Add(0x80);
        pcs.Add(0x00);
        pcs.Add(0x00);
        pcs.Add(0x01);
        AddU16(pcs, 0);
        pcs.Add(0x00);
        pcs.Add(0x00);
        AddU16(pcs, x);
        AddU16(pcs, y);
        Segment(s, pts, 0x16, pcs.ToArray());

        var wds = new List<byte>();
        wds.Add(0x01);
        wds.Add(0x00);
        AddU16(wds, x);
        AddU16(wds, y);
        AddU16(wds, w);
        AddU16(wds, h);
        Segment(s, pts, 0x17, wds.ToArray());

        var pds = new List<byte>();
        pds.Add(0x00);
        pds.Add(0x00);
        AddPalette(pds, 1, 235, 128, 128, 255);
        AddPalette(pds, 2, 235, 128, 128, 140);
        AddPalette(pds, 3, 16, 128, 128, 255);
        Segment(s, pts, 0x14, pds.ToArray());

        var ods = new List<byte>();
        AddU16(ods, 0);
        ods.Add(0x00);
        ods.Add(0xC0);
        int dataLen = rle.Length + 4;
        ods.Add((byte)((dataLen >> 16) & 0xFF));
        ods.Add((byte)((dataLen >> 8) & 0xFF));
        ods.Add((byte)(dataLen & 0xFF));
        AddU16(ods, w);
        AddU16(ods, h);
        ods.AddRange(rle);
        Segment(s, pts, 0x15, ods.ToArray());

        Segment(s, pts, 0x80, new byte[0]);
    }

    private static void WriteClear(Stream s, int ms, ushort compNumber)
    {
        uint pts = (uint)((long)ms * 90);

        var pcs = new List<byte>();
        AddU16(pcs, VideoWidth);
        AddU16(pcs, VideoHeight);
        pcs.Add(0x10);
        AddU16(pcs, compNumber + 1);
        pcs.Add(0x00);
        pcs.Add(0x00);
        pcs.Add(0x00);
        pcs.Add(0x00);
        Segment(s, pts, 0x16, pcs.ToArray());

        Segment(s, pts, 0x80, new byte[0]);
    }

    private static void AddPalette(List<byte> o, byte id, byte yv, byte cr, byte cb, byte a)
    {
        o.Add(id);
        o.Add(yv);
        o.Add(cr);
        o.Add(cb);
        o.Add(a);
    }

    private static void AddU16(List<byte> o, int v)
    {
        o.Add((byte)((v >> 8) & 0xFF));
        o.Add((byte)(v & 0xFF));
    }

    private static void Segment(Stream s, uint pts, byte type, byte[] payload)
    {
        var head = new byte[13];
        head[0] = 0x50;
        head[1] = 0x47;
        head[2] = (byte)((pts >> 24) & 0xFF);
        head[3] = (byte)((pts >> 16) & 0xFF);
        head[4] = (byte)((pts >> 8) & 0xFF);
        head[5] = (byte)(pts & 0xFF);
        head[6] = 0;
        head[7] = 0;
        head[8] = 0;
        head[9] = 0;
        head[10] = type;
        head[11] = (byte)((payload.Length >> 8) & 0xFF);
        head[12] = (byte)(payload.Length & 0xFF);
        s.Write(head, 0, head.Length);
        s.Write(payload, 0, payload.Length);
    }
}
