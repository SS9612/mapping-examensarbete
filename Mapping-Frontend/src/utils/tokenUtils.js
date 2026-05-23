import { jwtDecode } from "jwt-decode";

 // Decodes JWT token and returns payload

export function decodeToken(token) {
  try {
    return jwtDecode(token);
  } catch (error) {
    console.error("Failed to decode token:", error);
    return null;
  }
}

 // Checks if token is expired
 
export function isTokenExpired(token) {
  if (!token) return true;

  try {
    const decoded = decodeToken(token);
    if (!decoded || !decoded.exp) return true;

    const expirationTime = decoded.exp * 1000; 
    const currentTime = Date.now();
    const fiveMinutes = 5 * 60 * 1000;

    return expirationTime - currentTime < fiveMinutes;
  } catch (error) {
    console.error("Error checking token expiration:", error);
    return true;
  }
}

 //Gets token expiration time
 
export function getTokenExpiration(token) {
  if (!token) return null;

  try {
    const decoded = decodeToken(token);
    if (!decoded || !decoded.exp) return null;
    return new Date(decoded.exp * 1000);
  } catch (error) {
    console.error("Error getting token expiration:", error);
    return null;
  }
}

 // Validates token structure and expiration
 
export function isValidToken(token) {
  if (!token) return false;
  
  try {
    const decoded = decodeToken(token);
    if (!decoded) return false;

    // Check expiration
    if (decoded.exp) {
      const expirationTime = decoded.exp * 1000;
      if (Date.now() >= expirationTime) {
        return false;
      }
    }

    return true;
  } catch {
    return false;
  }
}