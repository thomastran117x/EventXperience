export interface User {
  Email: string;
  Username: string;
  /** The username as its owner wrote it. Render this; resolve and link by `Username`. */
  UsernameDisplay?: string | null;
  Name?: string | null;
  Avatar?: string | null;
  Usertype: string;
  Id: number;
}
