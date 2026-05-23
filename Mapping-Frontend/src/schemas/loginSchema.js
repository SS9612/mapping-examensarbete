import { z } from "zod";

export const loginSchema = z.object({
  username: z
    .string()
    .min(1, "Username is required")
    .max(100, "Username cannot exceed 100 characters"),
  password: z
    .string()
    .min(1, "Password is required")
    .max(200, "Password cannot exceed 200 characters"),
});