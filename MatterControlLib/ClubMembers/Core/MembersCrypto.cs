using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MatterHackers.MatterControl.ClubMembers
{
	/// <summary>
	/// A data key wrapped (encrypted) with a key made from the maintainer PIN.
	/// </summary>
	public sealed class WrappedKey
	{
		public int Iterations { get; set; }

		public byte[] Salt { get; set; }

		public byte[] Nonce { get; set; }

		public byte[] Ciphertext { get; set; }

		public byte[] Tag { get; set; }
	}

	public enum EnvelopeOpenStatus
	{
		Ok,
		WrongPin,
		Unreadable,
	}

	/// <summary>
	/// EBMC Edition: encryption of the members file. A random 256-bit data key encrypts the JSON with AES-GCM. The
	/// data key is wrapped, also with AES-GCM, by a key derived from the maintainer PIN with PBKDF2-HMAC-SHA256
	/// (own 16-byte salt, 100,000 iterations, the same count as the PIN's own hash).
	/// <para>The file is a small JSON object with exactly these members, in any order and with any white space:
	/// <c>{"format":"ebmc-members","v":1,"kdf":{"alg":"pbkdf2-sha256","iter":100000,"salt":B64},
	/// "wrap":{"nonce":B64,"ct":B64,"tag":B64},"data":{"nonce":B64,"ct":B64,"tag":B64}}</c>.
	/// Strings must be plain (no escapes), base64 canonical, sizes exact. The JSON is only a container: what is
	/// authenticated is built from the parsed values in this fixed byte layout, independent of any JSON writer:</para>
	/// <code>
	/// header   = "EBMC-MEMBERS-AAD" (16 ASCII bytes) | 0x01 (layout) | format version (uint32 big-endian)
	///            | 0x01 (KDF: PBKDF2-HMAC-SHA256) | iterations (uint32 big-endian) | salt length (1 byte) | salt
	/// wrap AAD = header | 'W'
	/// data AAD = header | 'D' | wrap nonce (12) | wrap ciphertext (32) | wrap tag (16)
	/// </code>
	/// <para>So changing any value of the file makes it fail to open. With a short PIN this guards against casual
	/// reading only; it is not strong security.</para>
	/// </summary>
	public static class MembersCrypto
	{
		public const string Format = "ebmc-members";
		public const int FormatVersion = 1;
		public const string KdfName = "pbkdf2-sha256";
		public const int Iterations = 100000;
		public const int KeyBytes = 32;
		public const int SaltBytes = 16;
		public const int NonceBytes = 12;
		public const int TagBytes = 16;

		/// <summary>The largest data part accepted when opening (a club's members are far smaller).</summary>
		public const int MaxDataBytes = 64 * 1024 * 1024;

		private const byte AadLayout = 1;
		private const byte KdfId = 1;
		private static readonly byte[] AadMagic = Encoding.ASCII.GetBytes("EBMC-MEMBERS-AAD");

		public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeyBytes);

		/// <summary>
		/// Encrypts <paramref name="plain"/>: returns nonce, ciphertext and tag, in that order.
		/// </summary>
		public static byte[] Seal(byte[] plain, byte[] dataKey, byte[] aad)
		{
			if (plain == null || dataKey == null || dataKey.Length != KeyBytes)
			{
				throw new ArgumentException("A 256-bit key and data are required.");
			}

			var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
			var cipher = new byte[plain.Length];
			var tag = new byte[TagBytes];
			using (var aes = CreateAesGcm(dataKey))
			{
				aes.Encrypt(nonce, plain, cipher, tag, aad);
			}

			var sealedBytes = new byte[NonceBytes + cipher.Length + TagBytes];
			Buffer.BlockCopy(nonce, 0, sealedBytes, 0, NonceBytes);
			Buffer.BlockCopy(cipher, 0, sealedBytes, NonceBytes, cipher.Length);
			Buffer.BlockCopy(tag, 0, sealedBytes, NonceBytes + cipher.Length, TagBytes);
			return sealedBytes;
		}

		/// <summary>
		/// Decrypts what <see cref="Seal"/> made. Null when the key, the associated data or any byte is wrong.
		/// </summary>
		public static byte[] Open(byte[] sealedBytes, byte[] dataKey, byte[] aad)
		{
			if (sealedBytes == null
				|| sealedBytes.Length < NonceBytes + TagBytes
				|| dataKey == null
				|| dataKey.Length != KeyBytes)
			{
				return null;
			}

			int cipherLength = sealedBytes.Length - NonceBytes - TagBytes;
			return Decrypt(
				dataKey,
				new ReadOnlySpan<byte>(sealedBytes, 0, NonceBytes),
				new ReadOnlySpan<byte>(sealedBytes, NonceBytes, cipherLength),
				new ReadOnlySpan<byte>(sealedBytes, NonceBytes + cipherLength, TagBytes),
				aad);
		}

		/// <summary>
		/// Wraps <paramref name="dataKey"/> with a key derived from <paramref name="pin"/> and a new random salt.
		/// </summary>
		public static WrappedKey WrapKey(byte[] dataKey, string pin)
		{
			if (dataKey == null || dataKey.Length != KeyBytes || string.IsNullOrEmpty(pin))
			{
				throw new ArgumentException("A 256-bit key and a PIN are required.");
			}

			var wrapped = new WrappedKey
			{
				Iterations = Iterations,
				Salt = RandomNumberGenerator.GetBytes(SaltBytes),
				Nonce = RandomNumberGenerator.GetBytes(NonceBytes),
				Ciphertext = new byte[KeyBytes],
				Tag = new byte[TagBytes],
			};

			var kek = DeriveKek(pin, wrapped.Salt, wrapped.Iterations);
			try
			{
				using (var aes = CreateAesGcm(kek))
				{
					aes.Encrypt(wrapped.Nonce, dataKey, wrapped.Ciphertext, wrapped.Tag, WrapAad(wrapped));
				}
			}
			finally
			{
				CryptographicOperations.ZeroMemory(kek);
			}

			return wrapped;
		}

		/// <summary>
		/// The data key, or null when <paramref name="pin"/> is wrong (or the wrapped key was changed).
		/// </summary>
		public static byte[] UnwrapKey(WrappedKey wrapped, string pin)
		{
			if (wrapped == null || string.IsNullOrEmpty(pin) || !IsWellFormed(wrapped))
			{
				return null;
			}

			var kek = DeriveKek(pin, wrapped.Salt, wrapped.Iterations);
			try
			{
				return UnwrapKeyWithKek(wrapped, kek);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(kek);
			}
		}

		/// <summary>
		/// The key-encryption key for a PIN and salt (the slow step).
		/// </summary>
		internal static byte[] DeriveKek(string pin, byte[] salt, int iterations)
		{
			var pinBytes = Encoding.UTF8.GetBytes(pin);
			try
			{
				return Rfc2898DeriveBytes.Pbkdf2(pinBytes, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(pinBytes);
			}
		}

		internal static byte[] UnwrapKeyWithKek(WrappedKey wrapped, byte[] kek)
		{
			if (wrapped == null || kek == null || !IsWellFormed(wrapped))
			{
				return null;
			}

			return Decrypt(kek, wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag, WrapAad(wrapped));
		}

		/// <summary>
		/// The whole file: <paramref name="plain"/> encrypted with <paramref name="dataKey"/>, plus the wrapped key.
		/// </summary>
		public static byte[] SealFile(byte[] plain, byte[] dataKey, WrappedKey wrapped)
		{
			var sealedData = Seal(plain, dataKey, DataAad(wrapped));
			int cipherLength = sealedData.Length - NonceBytes - TagBytes;

			// written by hand, not by a JSON writer: every value is a constant or base64 (plain ASCII), so nothing
			// is ever escaped and the bytes cannot change with a runtime's escaping rules
			var text = new StringBuilder();
			text.Append("{\"format\":\"").Append(Format).Append("\",\"v\":").Append(FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
				.Append(",\"kdf\":{\"alg\":\"").Append(KdfName)
				.Append("\",\"iter\":").Append(wrapped.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture))
				.Append(",\"salt\":\"").Append(Convert.ToBase64String(wrapped.Salt))
				.Append("\"},\"wrap\":{\"nonce\":\"").Append(Convert.ToBase64String(wrapped.Nonce))
				.Append("\",\"ct\":\"").Append(Convert.ToBase64String(wrapped.Ciphertext))
				.Append("\",\"tag\":\"").Append(Convert.ToBase64String(wrapped.Tag))
				.Append("\"},\"data\":{\"nonce\":\"").Append(Convert.ToBase64String(sealedData, 0, NonceBytes))
				.Append("\",\"ct\":\"").Append(Convert.ToBase64String(sealedData, NonceBytes, cipherLength))
				.Append("\",\"tag\":\"").Append(Convert.ToBase64String(sealedData, NonceBytes + cipherLength, TagBytes))
				.Append("\"}}");
			return Encoding.ASCII.GetBytes(text.ToString());
		}

		/// <summary>
		/// Reads the envelope strictly, field by field (exact member names, each once; types; plain strings;
		/// canonical base64; exact sizes; the fixed iteration count). False for anything else.
		/// </summary>
		public static bool TryParseEnvelope(byte[] fileBytes, out WrappedKey wrapped, out byte[] sealedData)
		{
			wrapped = null;
			sealedData = null;
			if (fileBytes == null || fileBytes.Length == 0 || fileBytes.Length > MaxDataBytes * 2)
			{
				return false;
			}

			try
			{
				using (var document = JsonDocument.Parse(fileBytes))
				{
					if (!TryReadObject(document.RootElement, new[] { "format", "v", "kdf", "wrap", "data" }, out var top)
						|| !TryReadPlainString(top["format"], out var format)
						|| format != Format
						|| !TryReadExactInt(top["v"], out int version)
						|| version != FormatVersion)
					{
						return false;
					}

					if (!TryReadObject(top["kdf"], new[] { "alg", "iter", "salt" }, out var kdf)
						|| !TryReadPlainString(kdf["alg"], out var alg)
						|| alg != KdfName
						|| !TryReadExactInt(kdf["iter"], out int iterations)
						|| iterations != Iterations
						|| !TryReadBase64(kdf["salt"], SaltBytes, SaltBytes, out var salt))
					{
						return false;
					}

					if (!TryReadObject(top["wrap"], new[] { "nonce", "ct", "tag" }, out var wrap)
						|| !TryReadBase64(wrap["nonce"], NonceBytes, NonceBytes, out var wrapNonce)
						|| !TryReadBase64(wrap["ct"], KeyBytes, KeyBytes, out var wrapCipher)
						|| !TryReadBase64(wrap["tag"], TagBytes, TagBytes, out var wrapTag))
					{
						return false;
					}

					if (!TryReadObject(top["data"], new[] { "nonce", "ct", "tag" }, out var data)
						|| !TryReadBase64(data["nonce"], NonceBytes, NonceBytes, out var dataNonce)
						|| !TryReadBase64(data["ct"], 0, MaxDataBytes, out var dataCipher)
						|| !TryReadBase64(data["tag"], TagBytes, TagBytes, out var dataTag))
					{
						return false;
					}

					wrapped = new WrappedKey
					{
						Iterations = iterations,
						Salt = salt,
						Nonce = wrapNonce,
						Ciphertext = wrapCipher,
						Tag = wrapTag,
					};

					sealedData = new byte[NonceBytes + dataCipher.Length + TagBytes];
					Buffer.BlockCopy(dataNonce, 0, sealedData, 0, NonceBytes);
					Buffer.BlockCopy(dataCipher, 0, sealedData, NonceBytes, dataCipher.Length);
					Buffer.BlockCopy(dataTag, 0, sealedData, NonceBytes + dataCipher.Length, TagBytes);
					return true;
				}
			}
			catch (JsonException)
			{
				return false;
			}
			catch (ArgumentException)
			{
				return false;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}

		/// <summary>
		/// Opens a whole file with the PIN. <paramref name="dataKey"/> and <paramref name="wrapped"/> are set when it
		/// opens, so the caller can save again without the PIN.
		/// </summary>
		public static EnvelopeOpenStatus OpenFile(byte[] fileBytes, string pin, out byte[] plain, out byte[] dataKey, out WrappedKey wrapped)
		{
			plain = null;
			dataKey = null;
			if (!TryParseEnvelope(fileBytes, out wrapped, out var sealedData))
			{
				wrapped = null;
				return EnvelopeOpenStatus.Unreadable;
			}

			var key = UnwrapKey(wrapped, pin);
			if (key == null)
			{
				wrapped = null;
				return EnvelopeOpenStatus.WrongPin;
			}

			plain = Open(sealedData, key, DataAad(wrapped));
			if (plain == null)
			{
				CryptographicOperations.ZeroMemory(key);
				wrapped = null;
				return EnvelopeOpenStatus.Unreadable;
			}

			dataKey = key;
			return EnvelopeOpenStatus.Ok;
		}

		/// <summary>
		/// Opens a whole file with a data key already known (from an earlier open). Null if it does not open.
		/// </summary>
		public static byte[] OpenFileWithKey(byte[] fileBytes, byte[] dataKey)
		{
			if (dataKey == null || !TryParseEnvelope(fileBytes, out var wrapped, out var sealedData))
			{
				return null;
			}

			return Open(sealedData, dataKey, DataAad(wrapped));
		}

		/// <summary>The authenticated header bytes (layout in the class summary).</summary>
		internal static byte[] HeaderAad(WrappedKey wrapped)
		{
			var header = new byte[AadMagic.Length + 1 + 4 + 1 + 4 + 1 + wrapped.Salt.Length];
			int offset = 0;
			Buffer.BlockCopy(AadMagic, 0, header, offset, AadMagic.Length);
			offset += AadMagic.Length;
			header[offset++] = AadLayout;
			BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset, 4), (uint)FormatVersion);
			offset += 4;
			header[offset++] = KdfId;
			BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset, 4), (uint)wrapped.Iterations);
			offset += 4;
			header[offset++] = (byte)wrapped.Salt.Length;
			Buffer.BlockCopy(wrapped.Salt, 0, header, offset, wrapped.Salt.Length);
			return header;
		}

		private static byte[] WrapAad(WrappedKey wrapped)
		{
			return Concat(HeaderAad(wrapped), new[] { (byte)'W' });
		}

		private static byte[] DataAad(WrappedKey wrapped)
		{
			return Concat(HeaderAad(wrapped), new[] { (byte)'D' }, wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag);
		}

		private static byte[] Concat(params byte[][] parts)
		{
			int length = 0;
			foreach (var part in parts)
			{
				length += part.Length;
			}

			var result = new byte[length];
			int offset = 0;
			foreach (var part in parts)
			{
				Buffer.BlockCopy(part, 0, result, offset, part.Length);
				offset += part.Length;
			}

			return result;
		}

		private static bool IsWellFormed(WrappedKey wrapped)
		{
			return wrapped.Iterations == Iterations
				&& wrapped.Salt?.Length == SaltBytes
				&& wrapped.Nonce?.Length == NonceBytes
				&& wrapped.Ciphertext?.Length == KeyBytes
				&& wrapped.Tag?.Length == TagBytes;
		}

		private static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, byte[] aad)
		{
			var plain = new byte[cipher.Length];
			try
			{
				using (var aes = CreateAesGcm(key))
				{
					aes.Decrypt(nonce, cipher, tag, plain, aad);
				}

				return plain;
			}
			catch (CryptographicException)
			{
				return null;
			}
		}

		private static AesGcm CreateAesGcm(byte[] key)
		{
#if NET8_0_OR_GREATER
			return new AesGcm(key, TagBytes);
#else
			return new AesGcm(key);
#endif
		}

		private static bool TryReadObject(JsonElement element, string[] names, out Dictionary<string, JsonElement> properties)
		{
			properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
			if (element.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			foreach (var property in element.EnumerateObject())
			{
				// exactly the expected names, each once
				if (Array.IndexOf(names, property.Name) < 0
					|| properties.ContainsKey(property.Name))
				{
					return false;
				}

				properties[property.Name] = property.Value;
			}

			return properties.Count == names.Length;
		}

		/// <summary>
		/// A string written without escape sequences: its raw text must be the value in quotes.
		/// </summary>
		private static bool TryReadPlainString(JsonElement element, out string value)
		{
			value = null;
			if (element.ValueKind != JsonValueKind.String)
			{
				return false;
			}

			value = element.GetString();
			return element.GetRawText() == "\"" + value + "\"";
		}

		private static bool TryReadExactInt(JsonElement element, out int value)
		{
			value = 0;
			return element.ValueKind == JsonValueKind.Number
				&& element.TryGetInt32(out value)
				&& element.GetRawText() == value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		private static bool TryReadBase64(JsonElement element, int minLength, int maxLength, out byte[] bytes)
		{
			bytes = null;
			if (!TryReadPlainString(element, out var text))
			{
				return false;
			}

			try
			{
				bytes = Convert.FromBase64String(text);
			}
			catch (FormatException)
			{
				return false;
			}

			// canonical only: base64 has spare bits in its last character, and a changed spare bit must not pass
			return bytes.Length >= minLength
				&& bytes.Length <= maxLength
				&& Convert.ToBase64String(bytes) == text;
		}
	}
}
