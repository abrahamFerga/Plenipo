import { useQuery } from "@tanstack/react-query";
import { api, type Me } from "../lib/api";

export function useMe() {
  return useQuery<Me>({
    queryKey: ["me"],
    queryFn: api.me,
  });
}
