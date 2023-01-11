// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.AspNetCore.Identity.Test;

public abstract class TokenManagerSpecificationTestBase<TUser> : TokenManagerSpecificationTestBase<TUser, string> where TUser : class { }

/// <summary>
/// Base class for tests that exercise basic identity functionality that all stores should support.
/// </summary>
/// <typeparam name="TUser">The type of the user.</typeparam>
/// <typeparam name="TKey">The primary key type.</typeparam>
public abstract class TokenManagerSpecificationTestBase<TUser, TKey>
    where TUser : class
    where TKey : IEquatable<TKey>
{
    /// <summary>
    /// Null value.
    /// </summary>
    protected const string NullValue = "(null)";

    /// <summary>
    /// Error describer.
    /// </summary>
    protected readonly IdentityErrorDescriber _errorDescriber = new IdentityErrorDescriber();
    private readonly string Issuer = "dotnet-user-jwts";
    private readonly string Audience = "<audience>";

    /// <summary>
    /// Configure the service collection used for tests.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="context"></param>
    protected virtual void SetupIdentityServices(IServiceCollection services, object context)
        => SetupBuilder(services, context);

    /// <summary>
    /// Configure the service collection used for tests.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="context"></param>
    protected virtual IdentityBuilder SetupBuilder(IServiceCollection services, object context)
    {
        services.AddHttpContextAccessor();
        // An example of what the expected schema looks like
        // "Authentication": {
        //     "Schemes": {
        //       "Identity.Bearer": {
        //         "Audiences": [ "", ""]
        //         "Issuer": "",
        // An example of what the expected signing keys (JWKs) looks like
        //"SigningCredentials": {
        //  "kty": "oct",
        //  "alg": "HS256",
        //  "kid": "randomguid",
        //  "k": "(G+KbPeShVmYq3t6w9z$C&F)J@McQfTj"
        //}
        //       }
        //     }
        //   }

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Authentication:Schemes:Identity.Bearer:Issuer"] = Issuer,
                ["Authentication:Schemes:Identity.Bearer:Audiences:0"] = Audience,
                ["Authentication:Schemes:Identity.Bearer:SigningCredentials:kty"] = "oct",
                ["Authentication:Schemes:Identity.Bearer:SigningCredentials:alg"] = "HS256",
                ["Authentication:Schemes:Identity.Bearer:SigningCredentials:kid"] = "someguid",
            })
            .Build());

        services.AddAuthentication();
        services.AddDataProtection();
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        var builder = services.AddDefaultIdentityBearer<TUser, IdentityStoreToken>(options =>
        {
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.User.AllowedUserNameCharacters = null;
        }).AddDefaultTokenProviders();
        AddUserStore(services, context);
        AddTokenStore(services, context);
        services.AddLogging();
        services.AddSingleton<ILogger<UserManager<TUser>>>(new TestLogger<UserManager<TUser>>());
        return builder;
    }

    protected abstract void AddUserStore(IServiceCollection services, object context = null);

    protected abstract void AddTokenStore(IServiceCollection services, object context = null);

    protected abstract void SetUserPasswordHash(TUser user, string hashedPassword);

    protected abstract TUser CreateTestUser(string namePrefix = "", string email = "", string phoneNumber = "",
        bool lockoutEnabled = false, DateTimeOffset? lockoutEnd = default, bool useNamePrefixAsUserName = false);

    protected abstract Expression<Func<TUser, bool>> UserNameEqualsPredicate(string userName);

    protected abstract Expression<Func<TUser, bool>> UserNameStartsWithPredicate(string userName);

    protected abstract object CreateTestContext();

    /// <summary>
    /// Creates the user manager used for tests.
    /// </summary>
    /// <param name="context">The context that will be passed into the store, typically a db context.</param>
    /// <param name="services">The service collection to use, optional.</param>
    /// <param name="configureServices">Delegate used to configure the services, optional.</param>
    /// <returns>The user manager to use for tests.</returns>
    protected virtual TokenManager<TUser, IdentityStoreToken> CreateManager(object context = null, IServiceCollection services = null, Action<IServiceCollection> configureServices = null)
        => CreateTestServices(context, services, configureServices).GetService<TokenManager<TUser, IdentityStoreToken>>();

    protected virtual IServiceProvider CreateTestServices(object context = null, IServiceCollection services = null, Action<IServiceCollection> configureServices = null)
    {
        services ??= new ServiceCollection();
        context ??= CreateTestContext();
        SetupIdentityServices(services, context);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task AccessTokenFormat()
    {
        var sp = CreateTestServices();
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var validator = sp.GetService<IAccessTokenValidator>();
        var user = CreateTestUser();
        var userId = await manager.UserManager.GetUserIdAsync(user);
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var claims = new[] {
            new Claim("custom", "value"),
            new Claim("custom2", "value"),
        };

        await manager.UserManager.AddClaimsAsync(user, claims);

        var token = await tokenService.GetAccessTokenAsync(user);
        Assert.NotNull(token);

        var principal = await validator.ValidateAsync(token);

        Assert.NotNull(principal);
        foreach (var cl in claims)
        {
            Assert.Contains(principal.Claims, c => c.Type == cl.Type && c.Value == cl.Value);
        }
        EnsureClaim(principal, "iss", Issuer);
        EnsureClaim(principal, "aud", Audience);
        EnsureClaim(principal, "sub", userId);

        Assert.NotNull(principal.Claims.FirstOrDefault(c => c.Type == TokenClaims.Jti));
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task AccessTokenDuplicateClaimsFormat()
    {
        var sp = CreateTestServices();
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var validator = sp.GetService<IAccessTokenValidator>();
        var user = CreateTestUser();
        var userId = await manager.UserManager.GetUserIdAsync(user);
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var claims = new[] {
            new Claim("custom", "value"),
            new Claim("custom", "value2"),
            new Claim("custom2", "value"),
            new Claim("custom2", "value2"),
        };

        await manager.UserManager.AddClaimsAsync(user, claims);

        var token = await tokenService.GetAccessTokenAsync(user);
        Assert.NotNull(token);

        var principal = await validator.ValidateAsync(token);

        Assert.NotNull(principal);
        EnsureClaim(principal, "custom", "value2");
        EnsureClaim(principal, "custom2", "value2");
        EnsureClaim(principal, "iss", Issuer);
        EnsureClaim(principal, "aud", Audience);
        EnsureClaim(principal, "sub", userId);

        Assert.NotNull(principal.Claims.FirstOrDefault(c => c.Type == TokenClaims.Jti));
    }

    /// <summary>
    /// Ensure that the principal has a claim with the name and value.
    /// </summary>
    /// <param name="principal"></param>
    /// <param name="name"></param>
    /// <param name="value"></param>
    protected void EnsureClaim(ClaimsPrincipal principal, string name, string value)
        => Assert.Contains(principal.Claims, c => c.Type == name && c.Value == value);

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task CanRefreshTokens()
    {
        var sp = CreateTestServices();
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var user = CreateTestUser();
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var token = await tokenService.GetRefreshTokenAsync(user);
        Assert.NotNull(token);

        (var access, var refresh) = await tokenService.RefreshTokensAsync(token);

        Assert.NotNull(access);
        Assert.NotNull(refresh);
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task CanStoreAccessTokens()
    {
        var sp = CreateTestServices();
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var validator = sp.GetService<IAccessTokenValidator>();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var user = CreateTestUser();
        var userId = await manager.UserManager.GetUserIdAsync(user);
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var claims = new[] {
            new Claim("custom", "value"),
            new Claim("custom2", "value"),
        };

        await manager.UserManager.AddClaimsAsync(user, claims);

        var token = await tokenService.GetAccessTokenAsync(user);
        Assert.NotNull(token);

        var principal = await validator.ValidateAsync(token);

        Assert.NotNull(principal);
        foreach (var cl in claims)
        {
            Assert.Contains(principal.Claims, c => c.Type == cl.Type && c.Value == cl.Value);
        }
        EnsureClaim(principal, "iss", Issuer);
        EnsureClaim(principal, "aud", Audience);
        EnsureClaim(principal, "sub", userId);

        var jti = principal.Claims.FirstOrDefault(c => c.Type == TokenClaims.Jti)?.Value;
        Assert.NotNull(jti);

        // Verify the token got serialized into the database
        var tok = await manager.FindByIdAsync<IDictionary<string, string>>(jti);
        Assert.NotNull(tok);

        // Make sure the payload is what we expect for the access token, with the security stamp
        var payload = tok.Payload as IDictionary<string, string>;
        Assert.NotNull(payload);
        Assert.NotNull(payload["AspNet.Identity.SecurityStamp"]);
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task CanRevokeAccessTokens()
    {
        var blockerOptions = new JtiBlockerOptions();
        var blocker = new JtiBlocker(Options.Create(blockerOptions));
        var sp = CreateTestServices(configureServices: o => o.AddSingleton<IAccessTokenDenyPolicy>(blocker));
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var validator = sp.GetService<IAccessTokenValidator>();
        var user = CreateTestUser();
        var userId = await manager.UserManager.GetUserIdAsync(user);
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var token = await tokenService.GetAccessTokenAsync(user);
        Assert.NotNull(token);

        var principal = await validator.ValidateAsync(token);

        Assert.NotNull(principal);
        EnsureClaim(principal, "iss", Issuer);
        EnsureClaim(principal, "aud", Audience);
        EnsureClaim(principal, "sub", userId);

        var jti = principal.Claims.FirstOrDefault(c => c.Type == TokenClaims.Jti)?.Value;
        Assert.NotNull(jti);

        // Revoke the access token and make sure it doesn't work
        blockerOptions.BlockedJti.Add(jti);
        Assert.Null(await validator.ValidateAsync(token));
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task ExpiredRefreshTokensFails()
    {
        var clock = new TestClock();
        var sp = CreateTestServices(configureServices: s => s.AddSingleton<ISystemClock>(clock));
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var user = CreateTestUser();
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var token = await tokenService.GetRefreshTokenAsync(user);
        Assert.NotNull(token);

        // Advance clock past expiration
        clock.UtcNow = DateTime.UtcNow.AddDays(2);

        (var access, var refresh) = await tokenService.RefreshTokensAsync(token);

        Assert.Null(access);
        Assert.Null(refresh);
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task CanPurgeExpiredTokens()
    {
        var clock = new TestClock();
        var manager = CreateManager(configureServices: s => s.AddSingleton<ISystemClock>(clock));
        var user = CreateTestUser();
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));
        var userId = await manager.UserManager.GetUserIdAsync(user);

        // Create a bunch of tokens some expired
        var expired1 = await manager.StoreAsync(new TokenInfo("id1", "f", userId, "p", "active") { Expiration = DateTimeOffset.UtcNow });
        var expired2 = await manager.StoreAsync(new TokenInfo("id2", "f", userId, "p", "active") { Expiration = DateTimeOffset.UtcNow });
        var token3 = await manager.StoreAsync(new TokenInfo("id3", "f", userId, "p", "active") { Expiration = DateTimeOffset.UtcNow.AddDays(1) });
        var token4 = await manager.StoreAsync(new TokenInfo("id4", "f", userId, "p", "active") { Expiration = DateTimeOffset.UtcNow.AddDays(1) });

        Assert.NotNull(await manager.FindByIdAsync<object>("id1"));
        Assert.NotNull(await manager.FindByIdAsync<object>("id2"));
        Assert.NotNull(await manager.FindByIdAsync<object>("id3"));
        Assert.NotNull(await manager.FindByIdAsync<object>("id4"));

        var purged = await manager.PurgeExpiredTokensAsync();

        // Make sure expired tokens are gone
        Assert.Equal(2, purged);
        Assert.Null(await manager.FindByIdAsync<object>("id1"));
        Assert.Null(await manager.FindByIdAsync<object>("id2"));
        Assert.NotNull(await manager.FindByIdAsync<object>("id3"));
        Assert.NotNull(await manager.FindByIdAsync<object>("id4"));
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task RevokedRefreshTokenFails()
    {
        var sp = CreateTestServices();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var user = CreateTestUser();
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var token = await tokenService.GetRefreshTokenAsync(user);
        Assert.NotNull(token);

        await tokenService.RevokeRefreshAsync(user, token);

        (var access, var refresh) = await tokenService.RefreshTokensAsync(token);

        Assert.Null(access);
        Assert.Null(refresh);
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task DeleteUserRemovesRefreshToken()
    {
        var sp = CreateTestServices();
        var tokenService = sp.GetService<IUserTokenService<TUser>>();
        var manager = sp.GetService<TokenManager<TUser, IdentityStoreToken>>();
        var user = CreateTestUser();
        IdentityResultAssert.IsSuccess(await manager.UserManager.CreateAsync(user));

        var token = await tokenService.GetRefreshTokenAsync(user);
        Assert.NotNull(token);

        IdentityResultAssert.IsSuccess(await manager.UserManager.DeleteAsync(user));
        var userId = await manager.UserManager.GetUserIdAsync(user);
        Assert.Null(await manager.UserManager.FindByIdAsync(userId));
        Assert.Null(await manager.Store.FindAsync("", token, CancellationToken.None));
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task CanStoreJWK()
    {
        var manager = CreateManager();

        var keyId = Guid.NewGuid().ToString();
        var data = new Dictionary<string, string>();
        data["kty"] = "oct";
        data["alg"] = "HS256";
        data["kid"] = keyId;
        data["k"] = "(G+KbPeShVmYq3t6w9z$C&F)J@McQfTj";
        var jwk = new JsonSigningKey(keyId, data);

        await manager.AddSigningKeyAsync(JsonKeySerializer.ProviderId, jwk);

        var key = await manager.GetSigningKeyAsync(keyId);

        Assert.NotNull(key);
        foreach (var k in data.Keys)
        {
            Assert.Equal(data[k], key[k]);
        }
    }

    /// <summary>
    /// Test.
    /// </summary>
    /// <returns>Task</returns>
    [Fact]
    public async Task CanStoreBase64Key()
    {
        var manager = CreateManager();

        var keyId = Guid.NewGuid().ToString();
        var base64Key = "(G+KbPeShVmYq3t6w9z$C&F)J@McQfTj";
        var baseKey = new Base64Key(keyId, base64Key);

        await manager.AddSigningKeyAsync(Base64KeySerializer.ProviderId, baseKey);

        var key = await manager.GetSigningKeyAsync(keyId);

        Assert.NotNull(key);
        foreach (var k in baseKey.Data.Keys)
        {
            Assert.Equal(baseKey.Data[k], key.Data[k]);
        }
    }
}