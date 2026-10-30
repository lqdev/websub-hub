module Tests

open System
open System.Net
open System.Text
open Xunit
open WebSubHub

let private config openMode prefixes =
    { HubUrl = Uri("https://hub.example/")
      AllowedPrefixes = prefixes
      OpenMode = openMode
      PublishToken = None
      AllowLocalUrls = false
      DefaultLease = 864000
      MinLease = 3600
      MaxLease = 2592000
      MaxTopicBytes = 5L * 1024L * 1024L }

[<Fact>]
let ``SHA-256 signature matches WebSub vector`` () =
    let body = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog")
    let actual = Signer.signSha256 "key" body
    Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", actual)
    Assert.True(Signer.verify "key" body actual)
    Assert.False(Signer.verify "other-key" body actual)

[<Fact>]
let ``Allowlist accepts exact host and path prefixes`` () =
    let policy = config false [ "https://lqdev.me/"; "https://lqdev.me/collections/" ]
    Assert.True(TopicPolicy.isAllowed policy (Uri("https://lqdev.me/.well-known/following.opml")))
    Assert.True(TopicPolicy.isAllowed policy (Uri("https://lqdev.me/collections/blogroll/index.opml")))

[<Fact>]
let ``Allowlist rejects hostname suffix attacks`` () =
    let policy = config false [ "https://lqdev.me/" ]
    Assert.False(TopicPolicy.isAllowed policy (Uri("https://lqdev.me.attacker.example/feed.xml")))

[<Fact>]
let ``Allowlist rejects sibling paths`` () =
    let policy = config false [ "https://lqdev.me/collections/" ]
    Assert.False(TopicPolicy.isAllowed policy (Uri("https://lqdev.me/collections-evil/feed.xml")))

[<Fact>]
let ``Policy rejects unsupported schemes and embedded credentials`` () =
    let policy = config true []
    Assert.False(TopicPolicy.isAllowed policy (Uri("ftp://lqdev.me/feed.xml")))
    let credentialUri = UriBuilder("https", "lqdev.me")
    credentialUri.UserName <- "test-user"
    Assert.False(TopicPolicy.isAllowed policy credentialUri.Uri)

[<Fact>]
let ``Network policy rejects loopback private and link-local addresses`` () =
    Assert.True(UriPolicy.isPrivateAddress(IPAddress.Parse("127.0.0.1")))
    Assert.True(UriPolicy.isPrivateAddress(IPAddress.Parse("10.0.0.1")))
    Assert.True(UriPolicy.isPrivateAddress(IPAddress.Parse("169.254.169.254")))
    Assert.True(UriPolicy.isPrivateAddress(IPAddress.Parse("fc00::1")))
    Assert.False(UriPolicy.isPrivateAddress(IPAddress.Parse("8.8.8.8")))

[<Fact>]
let ``Open mode accepts HTTP topics`` () =
    Assert.True(TopicPolicy.isAllowed (config true []) (Uri("https://feeds.example.org/feed.xml")))

[<Fact>]
let ``Lease duration is clamped`` () =
    let policy = config false []
    Assert.Equal(3600, Config.clampLease policy (Some 10))
    Assert.Equal(2592000, Config.clampLease policy (Some 4000000))
    Assert.Equal(864000, Config.clampLease policy None)

[<Fact>]
let ``Form parser preserves repeated publish URLs and decoded values`` () =
    let form = FormParsing.parse "hub.mode=publish&hub.url=https%3A%2F%2Flqdev.me%2Ffeed.xml&hub.url=https%3A%2F%2Flqdev.me%2Ffollowing.opml&hub.secret=a%2Bb"
    Assert.Equal<string list>(
        [ "https://lqdev.me/feed.xml"; "https://lqdev.me/following.opml" ],
        FormParsing.values form "hub.url"
    )
    Assert.Equal(Some "a+b", FormParsing.first form "hub.secret")

[<Fact>]
let ``Verification query appends to existing callback query`` () =
    let callback = Uri("https://subscriber.example/callback?token=redacted")
    let message =
        { Mode = "subscribe"
          Callback = callback.AbsoluteUri
          Topic = "https://lqdev.me/.well-known/following.opml"
          LeaseSeconds = 864000
          Secret = None }
    let result = Verification.makeVerificationUri callback message "challenge"
    Assert.Contains("token=redacted", result.Query)
    Assert.Contains("hub.challenge=challenge", result.Query)
