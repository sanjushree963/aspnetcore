// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Identity;

/// <summary>
/// Provides the APIs for managing tokens in a persistence store.
/// </summary>
/// <typeparam name="TUser">The type encapsulating a user.</typeparam>
/// <typeparam name="TToken">The type encapsulating a token.</typeparam>
public class TokenManager<TUser, TToken> : IDisposable
    where TUser : class
    where TToken : class
{
    private readonly IAccessTokenPolicy _accessTokenPolicy;
    private readonly ISystemClock _clock;
    private bool _disposed;

    /// <summary>
    /// The cancellation token used to cancel operations.
    /// </summary>
    protected internal virtual CancellationToken CancellationToken => CancellationToken.None;

    /// <summary>
    /// Constructs a new instance of <see cref="TokenManager{TUser,TToken}"/>.
    /// </summary>
    /// <param name="identityOptions">The options which configure the identity system.</param>
    /// <param name="store"></param>
    /// <param name="userManager">An instance of <see cref="UserManager{TUser}"/> used to retrieve users from and persist users.</param>
    /// <param name="errors">The <see cref="IdentityErrorDescriber"/> used to provider error messages.</param>
    /// <param name="logger">The logger used to log messages, warnings and errors.</param>
    /// <param name="claimsFactory">The factory to use to create claims principals for a user.</param>
    /// <param name="bearerOptions">The options which configure the bearer token such as signing key, audience, and issuer.</param>
    /// <param name="accessTokenPolicy"></param>
    /// <param name="clock"></param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public TokenManager(
        IOptions<IdentityOptions> identityOptions,
        ITokenStore<TToken> store,
        UserManager<TUser> userManager,
        IdentityErrorDescriber errors,
        ILogger<TokenManager<TUser,TToken>> logger,
        IAccessTokenClaimsFactory<TUser> claimsFactory,
        IOptions<IdentityBearerOptions> bearerOptions,
        IAccessTokenPolicy accessTokenPolicy,
        ISystemClock clock)
    {
        Options = identityOptions.Value.TokenManager;
        Store = store ?? throw new ArgumentNullException(nameof(store));
        UserManager = userManager;
        ErrorDescriber = errors;
        PayloadFactory = claimsFactory;
        Logger = logger;
        _accessTokenPolicy = accessTokenPolicy;
        _clock = clock;

        // TODO: Move these to registered named options?
        _keyFormatProviders[JsonKeySerializer.ProviderId] = new JsonKeySerializer();
        _keyFormatProviders[Base64KeySerializer.ProviderId] = new Base64KeySerializer();

        Options.FormatProviderMap[TokenFormat.JWT] = new JwtTokenFormat(_accessTokenPolicy, bearerOptions);
        Options.FormatProviderMap[TokenFormat.Single] = new GuidTokenFormat();

        Options.PurposeFormatMap[TokenPurpose.RefreshToken] = TokenFormat.Single;
        Options.PurposeFormatMap[TokenPurpose.AccessToken] = TokenFormat.JWT;
    }

    /// <summary>
    /// Gets the persistence store this instance operates over.
    /// </summary>
    /// <value>The persistence store this instance operates over.</value>
    protected internal ITokenStore<TToken> Store { get; }

    /// <summary>
    /// Gets the <see cref="ILogger"/> used to log messages from the manager.
    /// </summary>
    /// <value>
    /// The <see cref="ILogger"/> used to log messages from the manager.
    /// </value>
    public virtual ILogger Logger { get; set; }

    /// <summary>
    /// The <see cref="TokenManagerOptions"/>.
    /// </summary>
    public TokenManagerOptions Options { get; set; }

    /// <summary>
    /// The <see cref="UserManager{TUser}"/> used.
    /// </summary>
    public UserManager<TUser> UserManager { get; set; }

    /// <summary>
    /// The <see cref="IUserClaimsPrincipalFactory{TUser}"/> used.
    /// </summary>
    public IAccessTokenClaimsFactory<TUser> PayloadFactory { get; set; }

    /// <summary>
    /// Gets the <see cref="IdentityErrorDescriber"/> used to provider error messages.
    /// </summary>
    /// <value>
    /// The <see cref="IdentityErrorDescriber"/> used to provider error messages.
    /// </value>
    public IdentityErrorDescriber ErrorDescriber { get; set; }

    /// <summary>
    /// Releases all resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Find a token by its id.
    /// </summary>
    /// <param name="tokenId">The token id.</param>
    /// <returns>The <see cref="TokenInfo"/> representing the token</returns>
    public virtual async Task<TokenInfo?> FindByIdAsync<TPayload>(string tokenId)
    {
        var tok = await Store.FindByIdAsync(tokenId, CancellationToken).ConfigureAwait(false);
        if (tok == null)
        {
            return null;
        }

        return await Store.GetTokenInfoAsync<TPayload>(tok, CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes a token.
    /// </summary>
    /// <param name="tokenId">The token id to revoke.</param>
    /// <returns>The true if a token was revoked.</returns>
    public virtual async Task<bool> RevokeAsync(string tokenId)
    {
        var tok = await Store.FindByIdAsync(tokenId, CancellationToken).ConfigureAwait(false);
        if (tok == null)
        {
            return false;
        }

        await Store.SetStatusAsync(tok, TokenStatus.Revoked, CancellationToken).ConfigureAwait(false);
        var result = await Store.UpdateAsync(tok, CancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>
    /// Removes all expired tokens from the store
    /// </summary>
    /// <returns></returns>
    public virtual Task<int> PurgeExpiredTokensAsync()
        // TODO: Should add a filter to specify token purpose, etc?
        => Store.PurgeExpiredAsync(CancellationToken);

    /// <summary>
    /// Store a new token instance with the specified token info
    /// </summary>
    /// <param name="info">The <see cref="TokenInfo"/> for the token.</param>
    /// <returns></returns>
    public virtual async Task<TToken> StoreAsync(TokenInfo info)
    {
        // TODO: make internal?
        var tok = await Store.NewAsync(info, CancellationToken).ConfigureAwait(false);
        await Store.CreateAsync(tok, CancellationToken);
        return tok;
    }

    internal (string, ITokenFormatProvider) GetFormatProvider(string tokenPurpose)
    {
        // TODO: someone should be validating these
        var format = Options.PurposeFormatMap[tokenPurpose];
        if (!Options.FormatProviderMap.TryGetValue(format, out var provider))
        {
            throw new InvalidOperationException($"Could not find token format provider {format} registered for purpose: {tokenPurpose}.");
        }
        return (format, provider);
    }

    /// <summary>
    /// Check if the token status is valid. Defaults to only active token status.
    /// </summary>
    /// <param name="status">The token status.</param>
    /// <returns>true if the token is should be allowed.</returns>
    protected virtual bool CheckTokenStatus(string status)
        => status == TokenStatus.Active;

    private readonly IDictionary<string, IIdentityKeyDataSerializer> _keyFormatProviders = new Dictionary<string, IIdentityKeyDataSerializer>();

    // TODO: move these
    internal virtual async Task AddSigningKeyAsync(string keyProvider, SigningKeyInfo key)
    {
        if (!_keyFormatProviders.ContainsKey(keyProvider))
        {
            throw new InvalidOperationException($"Unknown format {keyProvider}.");
        }
        var provider = _keyFormatProviders[keyProvider];
        var keyData = provider.Serialize(key);

        await ((IKeyStore)Store).AddAsync(key.Id, provider.ProviderId, provider.Format, keyData, CancellationToken);
    }

    internal virtual async Task<SigningKeyInfo?> GetSigningKeyAsync(string keyId)
    {
        var keyData = await ((IKeyStore)Store).FindByIdAsync(keyId, CancellationToken);
        if (keyData == null)
        {
            return null;
        }
        if (!_keyFormatProviders.ContainsKey(keyData.ProviderId))
        {
            throw new InvalidOperationException($"Unknown format {keyData.Format}.");
        }
        var provider = _keyFormatProviders[keyData.ProviderId];
        return provider.Deserialize(keyData);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the role manager and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            //Store.Dispose();
        }
        _disposed = true;
    }

    /// <summary>
    /// Throws if this class has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}