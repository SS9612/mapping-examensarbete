import { toast } from "react-toastify";

 // Extracts error message from API error response
 // Handles both old format and new ApiResponse format
 
export function getErrorMessage(error) {
  if (!error) return "An unexpected error occurred";

  // Network error
  if (!error.response) {
    return error.message || "Network error. Please check your connection.";
  }

  const { data, status } = error.response;

  // Handle new ApiResponse format
  if (data?.message) {
    return data.message;
  }

  // Handle errors array from ApiResponse
  if (data?.errors && Array.isArray(data.errors) && data.errors.length > 0) {
    return data.errors.join(", ");
  }

  // Handle old format
  if (typeof data === "string") {
    return data;
  }

  if (data?.error) {
    return data.error;
  }

  // Default status-based messages
  const statusMessages = {
    400: "Invalid request. Please check your input.",
    401: "Unauthorized. Please log in again.",
    403: "You don't have permission to perform this action.",
    404: "The requested resource was not found.",
    500: "Server error. Please try again later.",
    503: "Service unavailable. Please try again later.",
  };

  return statusMessages[status] || `Error ${status}: ${error.message || "An error occurred"}`;
}

 // Shows error toast notification
 
export function showError(error, customMessage = null) {
  const message = customMessage || getErrorMessage(error);
  toast.error(message, {
    position: "top-right",
    autoClose: 5000,
    hideProgressBar: false,
    closeOnClick: true,
    pauseOnHover: true,
    draggable: true,
  });
}

 // Shows success toast notification
 
export function showSuccess(message) {
  toast.success(message, {
    position: "top-right",
    autoClose: 3000,
    hideProgressBar: false,
    closeOnClick: true,
    pauseOnHover: true,
    draggable: true,
  });
}


 // Shows info toast notification
   
export function showInfo(message) {
  toast.info(message, {
    position: "top-right",
    autoClose: 3000,
    hideProgressBar: false,
    closeOnClick: true,
    pauseOnHover: true,
    draggable: true,
  });
}

 // Wraps an async function with error handling

export async function handleAsync(fn, errorMessage = null) {
  try {
    return await fn();
  } catch (error) {
    showError(error, errorMessage);
    throw error;
  }
}