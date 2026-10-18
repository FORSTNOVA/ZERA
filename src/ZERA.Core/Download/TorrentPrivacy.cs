using System;
using System.IO;
using System.Text;

namespace ZERA.Core.Download
{
    // Read only the info/private flag without decoding binary piece hashes as text.
    internal sealed class TorrentPrivacy
    {
        private readonly byte[] _bytes;
        private int _position;
        private bool _private;
        private TorrentPrivacy(byte[] bytes) { _bytes = bytes; }
        public static bool IsPrivate(byte[] bytes)
        {
            try
            {
                var reader = new TorrentPrivacy(bytes);
                reader.Value(0, "");
                if (reader._position != bytes.Length) throw new FormatException();
                return reader._private;
            }
            catch (Exception ex) when (ex is FormatException || ex is IndexOutOfRangeException || ex is OverflowException)
            { throw new IOException("种子格式无效，无法读取私有标记。", ex); }
        }
        private string Text()
        {
            int start = _position;
            while (_bytes[_position] >= '0' && _bytes[_position] <= '9') _position++;
            if (_bytes[_position++] != ':' || !int.TryParse(Encoding.ASCII.GetString(_bytes, start, _position - start - 1), out int size) ||
                size < 0 || size > _bytes.Length - _position) throw new FormatException();
            string value = size <= 32 ? Encoding.ASCII.GetString(_bytes, _position, size) : "";
            _position += size;
            return value;
        }
        private void Value(int depth, string path)
        {
            if (depth > 64) throw new FormatException();
            byte type = _bytes[_position];
            if (type == 'd')
            {
                _position++;
                while (_bytes[_position] != 'e') { string key = Text(); Value(depth + 1, path + "/" + key); }
                _position++;
            }
            else if (type == 'l')
            { _position++; while (_bytes[_position] != 'e') Value(depth + 1, ""); _position++; }
            else if (type == 'i')
            {
                int start = ++_position;
                while (_bytes[_position] != 'e') _position++;
                if (!long.TryParse(Encoding.ASCII.GetString(_bytes, start, _position++ - start), out long value)) throw new FormatException();
                if (path == "/info/private" && value != 0) _private = true;
            }
            else if (type >= '0' && type <= '9') Text();
            else throw new FormatException();
        }
    }
}
