import { useQuery } from "@tanstack/react-query";
import { api, type PlatformInfo } from "../lib/api";

export function useInfo() {
  return useQuery<PlatformInfo>({
    queryKey: ["info"],
    queryFn: api.info,
    staleTime: Infinity, // deployment facts don't change within a session
  });
}
