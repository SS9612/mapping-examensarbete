// AUTH API - COMMENTED OUT - Auth disabled

export async function login(_username, _password) {
  // AUTH DISABLED - Return dummy response
  void _username;
  void _password;
  // const res = await client.post("/api/auth/login", {
  //   username,
  //   password,
  // });
  // return res.data;
  return { token: "dummy-token", username: "guest" };
}