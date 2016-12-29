using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TcpTunnel.Utils
{
    internal class CertificateUtils
    {
        /// <summary>
        /// Returns the X509 certificate with the given fingerprint from the current user's or the local machine's certificate store,
        /// or null if such a certificate was not found.
        /// 
        /// Note that it does not check if the user has permission to access the private key of the certificate.
        /// </summary>
        /// <param name="certFingerprint">the fingerprint in hex format (other characters will be filtered automatically)</param>
        /// <returns></returns>
        public static X509Certificate2 GetCurrentUserOrLocalMachineCertificateFromFingerprint(string certFingerprint)
        {
            // filter non-hex characters
            Regex reg = new Regex("[^0-9a-fA-F]+");
            certFingerprint = reg.Replace(certFingerprint, "");

            var locations = new StoreLocation[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine };
            X509Certificate2 resultCertificate = null;
            foreach (var location in locations)
            {
                using (X509Store store = new X509Store(location))
                {
                    store.Open(OpenFlags.ReadOnly);

                    // TODO: Dispose the other certificates.
                    var result = store.Certificates.Find(X509FindType.FindByThumbprint, certFingerprint, false);
                    if (result.Count != 0)
                    {
                        resultCertificate = result[0];
                    }
                }
            }

            return resultCertificate;
        }
    }
}
