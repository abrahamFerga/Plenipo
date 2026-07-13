import { useQuery } from "@tanstack/react-query";
import { api, type Module } from "../lib/api";

export function useModules() {
  return useQuery<Module[]>({
    queryKey: ["modules"],
    queryFn: api.modules,
  });
}
