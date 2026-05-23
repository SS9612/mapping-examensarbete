/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useState } from "react";
// import { useNavigate } from "react-router-dom"; // COMMENTED OUT - Auth disabled
// import { isValidToken, isTokenExpired } from "../utils/tokenUtils"; // COMMENTED OUT - Auth disabled

const AuthContext = createContext(null);

/**
 * Provides the auth contract expected by the UI while backend auth is disabled.
 *
 * Handoff note: this is not a real security boundary. It keeps route/components
 * stable until JWT auth is restored in both the API and axios client.
 */
export function AuthProvider({ children }) {
  // AUTH DISABLED - Always authenticated
  const [isAuthenticated] = useState(true); // Always true
  const [username] = useState("Guest"); // Default username
  const [loading] = useState(false); // No loading needed
  // const navigate = useNavigate(); // COMMENTED OUT - Auth disabled

  // useEffect(() => {
  //   const token = localStorage.getItem("token");
  //   const storedUsername = localStorage.getItem("username");
    
  //   if (token && storedUsername) {
  //     // Validate token before setting authenticated state
  //     if (isValidToken(token)) {
  //       if (isTokenExpired(token)) {
  //         // Token is expired or about to expire, clear it
  //         localStorage.removeItem("token");
  //         localStorage.removeItem("username");
  //         setIsAuthenticated(false);
  //         setUsername("");
  //       } else {
  //         setIsAuthenticated(true);
  //         setUsername(storedUsername);
  //       }
  //     } else {
  //       // Invalid token, clear it
  //       localStorage.removeItem("token");
  //       localStorage.removeItem("username");
  //       setIsAuthenticated(false);
  //       setUsername("");
  //     }
  //   }
  //   setLoading(false);
  // }, []);

  function logout() {
    // localStorage.removeItem("token"); // COMMENTED OUT - Auth disabled
    // localStorage.removeItem("username"); // COMMENTED OUT - Auth disabled
    // setIsAuthenticated(false); // COMMENTED OUT - Auth disabled
    // setUsername(""); // COMMENTED OUT - Auth disabled
    // navigate("/login"); // COMMENTED OUT - Auth disabled
    // No-op when auth is disabled
  }

  function login(_token, _username) {
    void _token;
    void _username;
    // localStorage.setItem("token", _token); // COMMENTED OUT - Auth disabled
    // localStorage.setItem("username", _username); // COMMENTED OUT - Auth disabled
    // setIsAuthenticated(true); // COMMENTED OUT - Auth disabled
    // setUsername(username); // COMMENTED OUT - Auth disabled
    // No-op when auth is disabled
  }

  return (
    <AuthContext.Provider value={{ isAuthenticated, username, loading, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return context;
}