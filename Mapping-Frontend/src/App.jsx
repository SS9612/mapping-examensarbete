import { BrowserRouter, Routes, Route, NavLink, Navigate } from "react-router-dom";
import { ToastContainer } from "react-toastify";
import "react-toastify/dist/ReactToastify.css";
import { AuthProvider } from "./contexts/AuthContext";
import { CompetenceProvider } from "./contexts/CompetenceContext";
import ErrorBoundary from "./components/ErrorBoundary";
import MapPage from "./pages/MapPageDev";
import ReviewPage from "./pages/ReviewPage";
// import LoginPage from "./pages/LoginPage"; // COMMENTED OUT - Auth disabled
import "./index.css"; 

/**
 * Route wrapper kept so protected routing can be restored without touching every page route.
 *
 * Handoff note: it currently always allows access because API auth is disabled.
 */
function ProtectedRoute({ children }) {
  // AUTH DISABLED - Always allow access
  // const { isAuthenticated, loading } = useAuth();

  // if (loading) {
  //   return <div className="loading">Loading...</div>;
  // }

  // if (!isAuthenticated) {
  //   return <Navigate to="/login" replace />;
  // }

  return children;
}

function AppContent() {
  return (
    <div className="app">
      <aside className="sidebar">
        <div className="sidebar-header">
          <h2>Mapping LIA</h2>
          <p className="sidebar-subtitle">
            Competence mapping dashboard
          </p>
        </div>
        <nav>
          <NavLink
            to="/map"
            className={({ isActive }) =>
              isActive ? "sidebar-link active" : "sidebar-link"
            }
          >
            Map competences
          </NavLink>
          <NavLink
            to="/review"
            className={({ isActive }) =>
              isActive ? "sidebar-link active" : "sidebar-link"
            }
          >
            Review competences
          </NavLink>
        </nav>
        <div className="sidebar-footer">
          {/* AUTH DISABLED - Commented out user info and logout */}
          {/* <div className="sidebar-user">Logged in as: {username}</div> */}
          {/* <button onClick={logout} className="btn btn-logout">
            Logout
          </button> */}
        </div>
      </aside>
      <main className="main">
        <Routes>
          {/* <Route path="/login" element={<LoginPage />} /> AUTH DISABLED - Login page commented out */}
          <Route
            path="/map"
            element={
              <ProtectedRoute>
                <MapPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/review"
            element={
              <ProtectedRoute>
                <ReviewPage />
              </ProtectedRoute>
            }
          />
          <Route path="*" element={<Navigate to="/review" replace />} />
        </Routes>
      </main>
    </div>
  );
}

export default function App() {
  return (
    <ErrorBoundary>
      <BrowserRouter>
        <AuthProvider>
          <CompetenceProvider>
            <AppContent />
            <ToastContainer />
          </CompetenceProvider>
        </AuthProvider>
      </BrowserRouter>
    </ErrorBoundary>
  );
}