// A minimal browser client for the sample: authorization code with PKCE, without a library.
// Production SPAs use a maintained library such as oidc-client-ts, or a BFF.
const base64Url = (bytes) =>
  btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const random = (length) => base64Url(crypto.getRandomValues(new Uint8Array(length)));

const config = await (await fetch("/spa/config.json")).json();
// Discovery, token and UserInfo are cross-origin: the provider allows this origin with CORS.
const metadata = await (await fetch(`${config.authority}/.well-known/openid-configuration`)).json();
const redirectUri = `${location.origin}/spa/`;

const signIn = document.getElementById("sign-in");
signIn.addEventListener("click", async () => {
  const verifier = random(32);
  const state = random(16);
  sessionStorage.setItem("spa-pkce", JSON.stringify({ verifier, state }));
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  const query = new URLSearchParams({
    client_id: config.clientId,
    response_type: "code",
    scope: config.scopes,
    redirect_uri: redirectUri,
    code_challenge: base64Url(new Uint8Array(digest)),
    code_challenge_method: "S256",
    state,
  });
  location.assign(`${metadata.authorization_endpoint}?${query}`);
});
// The button waits for the configuration and discovery above.
signIn.disabled = false;

const answer = new URLSearchParams(location.search);
if (answer.has("code")) {
  try {
    const { verifier, state } = JSON.parse(sessionStorage.getItem("spa-pkce") ?? "{}");
    sessionStorage.removeItem("spa-pkce");
    history.replaceState(null, "", redirectUri);
    if (answer.get("state") !== state) throw new Error("The authorization response has another state.");
    const tokens = await (
      await fetch(metadata.token_endpoint, {
        method: "POST",
        body: new URLSearchParams({
          grant_type: "authorization_code",
          code: answer.get("code"),
          redirect_uri: redirectUri,
          client_id: config.clientId,
          code_verifier: verifier,
        }),
      })
    ).json();
    if (!tokens.access_token) throw new Error(tokens.error ?? "No access token.");
    const user = await (
      await fetch(metadata.userinfo_endpoint, {
        headers: { Authorization: `Bearer ${tokens.access_token}` },
      })
    ).json();
    document.getElementById("spa-user").textContent = user.name;
  } catch (error) {
    document.getElementById("spa-error").textContent = String(error);
  }
}
