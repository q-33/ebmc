using System;
using System.Security.Cryptography;
using System.Text;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the club maintainer PIN. Only a salted PBKDF2-HMAC-SHA256 hash is stored, in the form
	/// <c>pbkdf2$100000$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>. Kept free of any GUI code so that it can be
	/// unit tested.
	/// </summary>
	public static class MaintainerPin
	{
		public const int MinLength = 4;

		public const int MaxLength = 8;

		public const int Iterations = 100000;

		private const string Scheme = "pbkdf2";

		private const int SaltBytes = 16;

		private const int HashBytes = 32;

		/// <summary>
		/// A PIN is 4 to 8 ASCII digits.
		/// </summary>
		public static bool IsValidPin(string pin)
		{
			if (pin == null
				|| pin.Length < MinLength
				|| pin.Length > MaxLength)
			{
				return false;
			}

			foreach (char c in pin)
			{
				if (c < '0' || c > '9')
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// The value to store for <paramref name="pin"/>, with a new random salt each time.
		/// </summary>
		public static string Hash(string pin)
		{
			if (pin == null)
			{
				throw new ArgumentNullException(nameof(pin));
			}

			byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
			byte[] hash = Derive(pin, salt, Iterations);

			return string.Join(
				"$",
				Scheme,
				Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
				Convert.ToBase64String(salt),
				Convert.ToBase64String(hash));
		}

		/// <summary>
		/// Whether <paramref name="pin"/> matches the <paramref name="stored"/> value made by <see cref="Hash"/>.
		/// False, never an exception, for a missing or malformed stored value or an iteration count this version
		/// does not use.
		/// </summary>
		public static bool Verify(string pin, string stored)
		{
			if (pin == null
				|| string.IsNullOrEmpty(stored))
			{
				return false;
			}

			try
			{
				string[] parts = stored.Split('$');
				if (parts.Length != 4
					|| parts[0] != Scheme
					|| parts[1] != Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture))
				{
					return false;
				}

				byte[] salt = Convert.FromBase64String(parts[2]);
				byte[] expected = Convert.FromBase64String(parts[3]);
				if (salt.Length != SaltBytes
					|| expected.Length != HashBytes)
				{
					return false;
				}

				byte[] actual = Derive(pin, salt, Iterations);
				return CryptographicOperations.FixedTimeEquals(actual, expected);
			}
			catch (FormatException)
			{
				return false;
			}
		}

		private static byte[] Derive(string pin, byte[] salt, int iterations)
		{
			return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
		}
	}
}
