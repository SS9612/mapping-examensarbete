/**
 * Maps backend error messages to user-friendly messages.
 * @param {string} errorMessage - The raw error message from the backend
 * @returns {string} User-friendly error message
 */
function mapErrorToUserFriendly(errorMessage) {
  if (!errorMessage) return errorMessage;

  if (errorMessage.includes("Duplicate competence already exists in database")) {
    return "This competence is already in the database";
  }
  if (errorMessage.includes("Input is too short or empty")) {
    return "This competence is too short or empty";
  }
  if (errorMessage.includes("Invalid input format")) {
    return "Invalid input format";
  }
  if (errorMessage.includes("Input could not be normalized")) {
    return "Could not normalize this competence";
  }
  if (errorMessage.includes("No matching area/category/subcategory found")) {
    return "No matching area/category/subcategory found";
  }
  if (errorMessage.includes("An error occurred during matching")) {
    return "An error occurred during matching. Please try again or contact support if the issue persists.";
  }
  if (errorMessage.includes("Validation failed")) {
    return "Validation failed: input may be misspelled, non-English, or the match is incorrect.";
  }

  return errorMessage;
}

/**
 * Parses a single error text from the backend format "competence: error message"
 * and returns a formatted user-friendly message.
 * @param {string} errorText - Error text in format "competence: error message" or just "error message"
 * @returns {string} Formatted error message with competence name and user-friendly error
 */
export function parseErrorMessage(errorText) {
  if (!errorText) return errorText;

  // Error format from backend: "competence: error message"
  const colonIndex = errorText.indexOf(": ");
  const competence = colonIndex > 0 ? errorText.substring(0, colonIndex).trim() : null;
  const errorMessage = colonIndex > 0 ? errorText.substring(colonIndex + 2).trim() : errorText.trim();

  const friendlyMessage = mapErrorToUserFriendly(errorMessage);

  // Return formatted message with competence name if available
  if (competence) {
    return `${competence}: ${friendlyMessage}`;
  }
  return friendlyMessage;
}

/**
 * Extracts error messages from API error responses.
 * Handles different response formats (400 BadRequest, 207 Multi-Status, etc.)
 * @param {Error} error - The error object from axios/API call
 * @returns {string[]} Array of parsed error messages
 */
export function extractErrorMessages(error) {
  if (!error) return [];

  // Handle 400 BadRequest - error.response.data is a string
  if (error.response?.status === 400) {
    const errorData = error.response.data;
    if (typeof errorData === "string") {
      // Backend returns errors joined by "; " when all fail
      return errorData.split("; ").map(parseErrorMessage);
    }
    // Sometimes it might be an object with a message
    if (errorData?.message) {
      return [parseErrorMessage(errorData.message)];
    }
  }

  // Handle 207 Multi-Status - partial success with errors
  if (error.response?.status === 207) {
    const errorData = error.response.data;
    if (errorData?.Errors && Array.isArray(errorData.Errors)) {
      return errorData.Errors.map(parseErrorMessage);
    }
    if (errorData?.errors && Array.isArray(errorData.errors)) {
      return errorData.errors.map(parseErrorMessage);
    }
  }

  // Handle successful response with errors (207 status code)
  if (error.response?.data) {
    const data = error.response.data;
    if (data.Errors && Array.isArray(data.Errors)) {
      return data.Errors.map(parseErrorMessage);
    }
    if (data.errors && Array.isArray(data.errors)) {
      return data.errors.map(parseErrorMessage);
    }
  }

  // Fallback to generic error message
  return [error.message || "An unexpected error occurred"];
}

/**
 * Extracts errors from a successful API response (e.g., 207 Multi-Status).
 * @param {Object|Array} data - The response data from the API
 * @returns {string[]} Array of parsed error messages
 */
export function extractErrorsFromResponse(data) {
  if (!data) return [];

  const errors = !Array.isArray(data) ? (data.errors || data.Errors || []) : [];
  return errors.map(parseErrorMessage);
}