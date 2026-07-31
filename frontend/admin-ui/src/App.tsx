import { BrowserRouter } from "react-router-dom";
import { AdminConsoleShell } from "./components/AdminConsoleShell";

export default function App() {
  return (
    // The console is served under /admin (see vite `base`), so the router is based there too. Deep links
    // like /admin/users resolve to the right section both on the dev server and when served by the API host.
    <BrowserRouter basename="/admin">
      <AdminConsoleShell />
    </BrowserRouter>
  );
}
