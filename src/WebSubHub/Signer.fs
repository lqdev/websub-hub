namespace WebSubHub

open System
open System.Security.Cryptography
open System.Text

module Signer =
    let signSha256 (secret: string) (body: byte array) =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
        "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant()

    let verify (secret: string) (body: byte array) (header: string) =
        let expected = signSha256 secret body
        let expectedBytes = Encoding.ASCII.GetBytes(expected)
        let actualBytes = Encoding.ASCII.GetBytes(header.Trim())

        expectedBytes.Length = actualBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)
