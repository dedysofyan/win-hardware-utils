using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Collections.Generic;

class PipeProbe {
    // FDG_PROTOCOL framing for small messages: "FDG_PROTOCOL\n" + 4-byte LE length + payload
    const string HDR = "FDG_PROTOCOL\n";

    static byte[] BuildIpcCommandGetDeviceDetail(string deviceCode, string uid) {
        // GetDeviceDetailInfoParams { deviceCode = ?, uid = ? }
        var inner = new MemoryStream();
        WriteString(inner, 1, deviceCode);    // field 1 = deviceCode
        WriteString(inner, 2, uid);            // field 2 = uid

        var env = new MemoryStream();
        WriteVarintField(env, 1, 4099);        // cmdId = GetDeviceDetailInfo = 4099
        WriteVarintField(env, 2, 1);           // category = Controller = 1
        WriteLengthDelimitedField(env, 4, inner.ToArray());  // field 4 = getDeviceDetailInfoParam
        return env.ToArray();
    }

    static void WriteTag(Stream s, int fieldNum, int wireType) {
        WriteVarint(s, ((uint)fieldNum << 3) | (uint)wireType);
    }
    static void WriteVarint(Stream s, uint v) {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }
    static void WriteVarintField(Stream s, int fieldNum, uint value) {
        WriteTag(s, fieldNum, 0); WriteVarint(s, value);
    }
    static void WriteString(Stream s, int fieldNum, string str) {
        var b = Encoding.UTF8.GetBytes(str);
        WriteTag(s, fieldNum, 2); WriteVarint(s, (uint)b.Length);
        s.Write(b, 0, b.Length);
    }
    static void WriteLengthDelimitedField(Stream s, int fieldNum, byte[] data) {
        WriteTag(s, fieldNum, 2); WriteVarint(s, (uint)data.Length);
        s.Write(data, 0, data.Length);
    }

    static byte[] ReadExact(Stream s, int n) {
        byte[] buf = new byte[n];
        int off = 0;
        while (off < n) {
            int r = s.Read(buf, off, n - off);
            if (r <= 0) throw new IOException("unexpected EOF after " + off + " of " + n);
            off += r;
        }
        return buf;
    }
    static string ReadHeaderLine(Stream s) {
        // Read bytes until '\n', expecting an ASCII line like "FDG_PROTOCOL", "FDG_COMPRESSED", "FDG_CHUNK".
        var sb = new StringBuilder();
        byte[] one = new byte[1];
        while (sb.Length < 64) {
            int r = s.Read(one, 0, 1);
            if (r <= 0) return null;
            if (one[0] == (byte)'\n') return sb.ToString();
            sb.Append((char)one[0]);
        }
        return null;
    }

    static byte[] Frame(byte[] payload) {
        var ms = new MemoryStream();
        var hdrBytes = Encoding.UTF8.GetBytes(HDR);
        ms.Write(hdrBytes, 0, hdrBytes.Length);
        var len = BitConverter.GetBytes(payload.Length);
        ms.Write(len, 0, 4);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    static int Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: PipeProbe.exe <deviceCode> <uid>");
            Console.WriteLine("Example: PipeProbe.exe k2 <your-apex-uid-from-flydigi-main-log>");
            return 1;
        }
        string deviceCode = args[0];
        string uid = args[1];

        var payload = BuildIpcCommandGetDeviceDetail(deviceCode, uid);
        var wire = Frame(payload);

        Console.WriteLine("Request payload (" + payload.Length + " bytes): " + BitConverter.ToString(payload));
        Console.WriteLine("Wire (" + wire.Length + " bytes): " + BitConverter.ToString(wire));

        using (var pipe = new NamedPipeClientStream(".", "fcs.sock", PipeDirection.InOut)) {
            try { pipe.Connect(15000); }
            catch (Exception ex) { Console.WriteLine("Connect failed: " + ex.Message); return 2; }
            Console.WriteLine("connected.");

            pipe.Write(wire, 0, wire.Length);
            pipe.Flush();
            Console.WriteLine("sent.");

            // Read ONE framed response and exit. "FDG_<TYPE>\n" + 4-byte LE len + payload.
            string hdr = ReadHeaderLine(pipe);
            if (hdr == null) { Console.WriteLine("no header"); return 3; }
            Console.WriteLine("header: " + hdr);
            byte[] lenBuf = ReadExact(pipe, 4);
            int len = BitConverter.ToInt32(lenBuf, 0);
            Console.WriteLine("payload length: " + len);
            byte[] data = ReadExact(pipe, len);
            Console.WriteLine("read payload " + data.Length + " bytes, kind=" + hdr);
            if (hdr.StartsWith("FDG_COMPRESSED")) {
                using (var ms = new MemoryStream(data, 0, data.Length))
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                using (var outMs = new MemoryStream()) {
                    gz.CopyTo(outMs);
                    data = outMs.ToArray();
                    Console.WriteLine("decompressed: " + data.Length + " bytes");
                }
            }
            Console.WriteLine("\n=== RESPONSE (" + data.Length + " bytes) ===");
            // Hex dump
            for (int i = 0; i < data.Length; i += 16) {
                var sb = new StringBuilder();
                sb.AppendFormat("{0:X4}  ", i);
                for (int j = 0; j < 16; j++) {
                    if (i + j < data.Length) sb.AppendFormat("{0:X2} ", data[i + j]);
                    else sb.Append("   ");
                }
                sb.Append(" ");
                for (int j = 0; j < 16 && i + j < data.Length; j++) {
                    byte b = data[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine(sb.ToString());
            }
        }
        return 0;
    }
}
