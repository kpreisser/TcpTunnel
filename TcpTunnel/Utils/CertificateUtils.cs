using System;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace TcpTunnel.Utils;

internal class CertificateUtils
{
    private static readonly Regex filterRegex = new("[^0-9a-fA-F]+");

    /// <summary>
    /// Returns the X509 certificate with the given fingerprint from the current user's or
    /// the local machine's "My" certificate store, or null if such a certificate was not found.
    /// 
    /// Note that it does not check if the user has permission to access the private key of
    /// the certificate.
    /// </summary>
    /// <param name="certFingerprint">
    /// the fingerprint in hex format (other characters will be filtered automatically)
    /// </param>
    /// <returns></returns>
    public static X509Certificate2 GetCurrentUserOrLocalMachineCertificateFromFingerprint(
        string certFingerprint)
    {
        // filter non-hex characters
        certFingerprint = filterRegex.Replace(certFingerprint, "");

        var locations = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine };

        foreach (var location in locations)
        {
            using (var store = new X509Store(StoreName.My, location))
            {
                store.Open(OpenFlags.ReadOnly);

                // TODO: Dispose the other certificates.
                var result = store.Certificates.Find(X509FindType.FindByThumbprint, certFingerprint, false);

                if (result.Count is not 0)
                {
                    var resultCertificate = result[0];

                    if (!resultCertificate.HasPrivateKey)
                        throw new System.Exception("Certificate doesn't have a private key.");

                    return resultCertificate;
                }
            }
        }

        throw new ArgumentException($"Could not find certificate with thumbprint '{certFingerprint}'.");
    }
}
