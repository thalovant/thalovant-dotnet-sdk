// Derived from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.
#nullable disable
namespace Thalovant.Internal.BouncyCastle.Crypto
{
    /// <summary>
    /// Interface for a converter that produces a byte encoding for a char array.
    /// </summary>
    internal interface ICharToByteConverter
    {
        /// <summary>The name of the conversion.</summary>
        string Name { get; }

        /// <summary>Return a byte encoded representation of the passed in password.</summary>
        /// <param name="password">the characters to encode.</param>
        /// <return>a byte encoding of password.</return>
        byte[] Convert(char[] password);
    }
}
