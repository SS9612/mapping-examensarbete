// LOGIN PAGE - COMMENTED OUT - Auth disabled
// import { useEffect, useState } from "react";
// import { useForm } from "react-hook-form";
// import { zodResolver } from "@hookform/resolvers/zod";
// import { useNavigate } from "react-router-dom";
// import { useAuth } from "../contexts/AuthContext";
// import { login } from "../api/authApi";
// import { showError, showSuccess } from "../utils/errorHandler";
// import { loginSchema } from "../schemas/loginSchema";
// import "./LoginPage.css";

export default function LoginPage() {
  // AUTH DISABLED - Redirect to review page
  // const navigate = useNavigate();
  // const { login: authLogin, isAuthenticated } = useAuth();
  // const {
  //   register,
  //   handleSubmit,
  //   formState: { errors, isSubmitting },
  // } = useForm({
  //   resolver: zodResolver(loginSchema),
  //   mode: "onBlur",
  // });

  // useEffect(() => {
  //   if (isAuthenticated) {
  //     navigate("/review", { replace: true });
  //   }
  // }, [isAuthenticated, navigate]);

  // const [showPassword, setShowPassword] = useState(false);

  // async function onSubmit(data) {
  //   try {
  //     const response = await login(data.username, data.password);
  //     authLogin(response.token, response.username);
  //     showSuccess("Login successful!");
  //     navigate("/review");
  //   } catch (err) {
  //     const errorMsg = err.response?.data?.message || err.response?.data?.errors?.[0] || "Login failed. Please check your credentials.";
  //     showError(err, errorMsg);
  //   }
  // }

  // return (
  //   <div className="login-page">
  //     <div className="login-container">
  //       <h1>Mapping LIA</h1>
  //       <h2>Staff login</h2>
  //       <p className="login-subtitle">
  //         Sigma Technologies competence mapping dashboard
  //       </p>
  //       <form onSubmit={handleSubmit(onSubmit)} className="login-form">
  //         <div className="form-group">
  //           <label htmlFor="username">Username</label>
  //           <input
  //             id="username"
  //             type="text"
  //             {...register("username")}
  //             autoFocus
  //             disabled={isSubmitting}
  //             className={errors.username ? "error" : ""}
  //           />
  //           {errors.username && (
  //             <span className="form-error">{errors.username.message}</span>
  //           )}
  //         </div>
  //         <div className="form-group">
  //           <label htmlFor="password">Password</label>
  //           <div className="password-field">
  //             <input
  //               id="password"
  //               type={showPassword ? "text" : "password"}
  //               {...register("password")}
  //               disabled={isSubmitting}
  //               className={errors.password ? "error" : ""}
  //               autoComplete="current-password"
  //             />
  //             <button
  //               type="button"
  //               className="btn btn-sm btn-ghost password-toggle"
  //               onClick={() => setShowPassword(v => !v)}
  //               aria-label={showPassword ? "Hide password" : "Show password"}
  //             >
  //               {showPassword ? "Hide" : "Show"}
  //             </button>
  //           </div>
  //           {errors.password && (
  //             <span className="form-error">{errors.password.message}</span>
  //           )}
  //         </div>
  //         <button type="submit" className="btn btn-primary btn-lg login-submit" disabled={isSubmitting}>
  //           {isSubmitting ? "Logging in..." : "Login"}
  //         </button>
  //       </form>
  //     </div>
  //   </div>
  // );

  // AUTH DISABLED - Just redirect
  return null;
}