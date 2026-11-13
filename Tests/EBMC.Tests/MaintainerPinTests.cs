using System;
using MatterHackers.MatterControl;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class MaintainerPinTests
	{
		[TestCase("1234")]
		[TestCase("0000")]
		[TestCase("12345678")]
		public void RoundTrip(string pin)
		{
			string stored = MaintainerPin.Hash(pin);

			Assert.That(MaintainerPin.Verify(pin, stored), Is.True);
		}

		[Test]
		public void StoredFormat()
		{
			string[] parts = MaintainerPin.Hash("1234").Split('$');

			Assert.That(parts, Has.Length.EqualTo(4));
			Assert.That(parts[0], Is.EqualTo("pbkdf2"));
			Assert.That(parts[1], Is.EqualTo("100000"));
			Assert.That(Convert.FromBase64String(parts[2]), Has.Length.EqualTo(16));
			Assert.That(Convert.FromBase64String(parts[3]), Has.Length.EqualTo(32));
		}

		[Test]
		public void WrongPin()
		{
			string stored = MaintainerPin.Hash("1234");

			Assert.That(MaintainerPin.Verify("1235", stored), Is.False);
			Assert.That(MaintainerPin.Verify("12345", stored), Is.False);
			Assert.That(MaintainerPin.Verify("", stored), Is.False);
			Assert.That(MaintainerPin.Verify(null, stored), Is.False);
		}

		[Test]
		public void TwoHashesOfOnePinDiffer()
		{
			string first = MaintainerPin.Hash("1234");
			string second = MaintainerPin.Hash("1234");

			Assert.That(first, Is.Not.EqualTo(second));
			Assert.That(MaintainerPin.Verify("1234", first), Is.True);
			Assert.That(MaintainerPin.Verify("1234", second), Is.True);
		}

		[TestCase(null)]
		[TestCase("")]
		[TestCase("1234")]
		[TestCase("pbkdf2")]
		[TestCase("pbkdf2$100000$$")]
		[TestCase("pbkdf2$100000$not base64!$also not")]
		[TestCase("pbkdf2$100000$AAAA$AAAA")]
		[TestCase("sha1$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
		[TestCase("pbkdf2$abc$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
		[TestCase("pbkdf2$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=$extra")]
		public void MalformedStoredValueIsFalse(string stored)
		{
			Assert.That(MaintainerPin.Verify("1234", stored), Is.False);
		}

		[Test]
		public void UnsupportedIterationCountIsFalse()
		{
			string[] parts = MaintainerPin.Hash("1234").Split('$');
			parts[1] = "1000";

			Assert.That(MaintainerPin.Verify("1234", string.Join("$", parts)), Is.False);
		}

		[TestCase("1234", true)]
		[TestCase("12345678", true)]
		[TestCase("0000", true)]
		[TestCase("123", false)]
		[TestCase("123456789", false)]
		[TestCase("", false)]
		[TestCase(null, false)]
		[TestCase("12a4", false)]
		[TestCase("12 34", false)]
		[TestCase(" 1234", false)]
		[TestCase("-123", false)]
		[TestCase("١٢٣٤", false)] // Arabic-Indic digits are not ASCII digits
		public void IsValidPin(string pin, bool expected)
		{
			Assert.That(MaintainerPin.IsValidPin(pin), Is.EqualTo(expected));
		}
	}
}
