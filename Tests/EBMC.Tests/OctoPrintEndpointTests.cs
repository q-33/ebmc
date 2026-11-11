using System;
using MatterHackers.MatterControl.OctoPrint;
using NUnit.Framework;

namespace EBMC.Tests
{
	[TestFixture]
	public class OctoPrintEndpointTests
	{
		[TestCase("octopi.local", "http://octopi.local/")]
		[TestCase("  octopi.local  ", "http://octopi.local/")]
		[TestCase("octopi.local:5000", "http://octopi.local:5000/")]
		[TestCase("192.168.1.20", "http://192.168.1.20/")]
		[TestCase("https://x:8443/octo", "https://x:8443/octo/")]
		[TestCase("HTTP://OctoPi.Local", "http://octopi.local/")]
		[TestCase("http://pi:80/", "http://pi/")]
		[TestCase("https://pi:443", "https://pi/")]
		[TestCase("http://user:secret@pi/octoprint/?apikey=abc#frag", "http://pi/octoprint/")]
		[TestCase("http://pi/octoprint?x=1", "http://pi/octoprint/")]
		[TestCase("http://[::1]:5000", "http://[::1]:5000/")]
		public void NormalizesAddresses(string input, string expected)
		{
			Assert.That(OctoPrintEndpoint.TryNormalize(input, out Uri baseUri, out string error), Is.True, error);
			Assert.That(baseUri.AbsoluteUri, Is.EqualTo(expected));
			Assert.That(error, Is.Null);
		}

		[TestCase("ftp://x")]
		[TestCase("file:///etc/passwd")]
		[TestCase("")]
		[TestCase("   ")]
		[TestCase(null)]
		[TestCase("http://")]
		[TestCase("http://exa mple.com")]
		public void RejectsInvalidAddresses(string input)
		{
			Assert.That(OctoPrintEndpoint.TryNormalize(input, out Uri baseUri, out string error), Is.False);
			Assert.That(baseUri, Is.Null);
			Assert.That(error, Is.Not.Null.And.Not.Empty);
		}

		[TestCase("http:/octopi.local")]
		[TestCase("http:octopi.local")]
		[TestCase("https:/pi:443/octo")]
		[TestCase("HTTP:pi")]
		public void RejectsSchemeWithoutSlashes(string input)
		{
			Assert.That(OctoPrintEndpoint.TryNormalize(input, out Uri baseUri, out string error), Is.False);
			Assert.That(baseUri, Is.Null);
			Assert.That(error, Does.Contain("malformed"));
		}

		[TestCase("http://[fe80::1%25en0]:5000/")]
		[TestCase("[fe80::1%25en0]:5000")]
		public void RejectsLinkLocalIpv6WithZone(string input)
		{
			Assert.That(OctoPrintEndpoint.TryNormalize(input, out Uri baseUri, out string error), Is.False);
			Assert.That(baseUri, Is.Null);
			Assert.That(error, Does.Contain("Link-local IPv6").And.Contain("host name"));
		}

		[TestCase("http://pi/../")]
		[TestCase("http://pi/octo/../x")]
		[TestCase("http://pi/%2e%2e/")]
		[TestCase("http://pi/%2E%2e/api")]
		[TestCase("http://pi/./octo")]
		[TestCase("http://pi/octo/%2e")]
		[TestCase("pi/a/..")]
		[TestCase("http://pi\\..\\x")]
		public void RejectsDotSegmentsInTheBasePath(string input)
		{
			Assert.That(OctoPrintEndpoint.TryNormalize(input, out Uri baseUri, out string error), Is.False);
			Assert.That(baseUri, Is.Null);
			Assert.That(error, Does.Contain("segments"));
		}

		[TestCase("http://pi/a..b/", "http://pi/a..b/")]
		[TestCase("http://pi/.well/", "http://pi/.well/")]
		[TestCase("http://pi/octo?x=../..", "http://pi/octo/")]
		public void AllowsDotsThatAreNotSegments(string input, string expected)
		{
			Assert.That(OctoPrintEndpoint.TryNormalize(input, out Uri baseUri, out string error), Is.True, error);
			Assert.That(baseUri.AbsoluteUri, Is.EqualTo(expected));
		}

		[Test]
		public void EndpointsResolveUnderTheBasePath()
		{
			Assert.That(OctoPrintEndpoint.TryNormalize("http://pi/octoprint", out Uri baseUri, out _), Is.True);
			Assert.That(new Uri(baseUri, "api/version").AbsoluteUri, Is.EqualTo("http://pi/octoprint/api/version"));
		}

		[Test]
		public void KeyIsStableForEquivalentInput()
		{
			OctoPrintEndpoint.TryNormalize("OctoPi.local", out Uri a, out _);
			OctoPrintEndpoint.TryNormalize("http://octopi.local/?x=y", out Uri b, out _);
			Assert.That(OctoPrintEndpoint.Key(a), Is.EqualTo("http://octopi.local/"));
			Assert.That(OctoPrintEndpoint.Key(b), Is.EqualTo(OctoPrintEndpoint.Key(a)));
		}
	}
}
